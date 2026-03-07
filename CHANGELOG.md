# Changelog

## [0.13.32] - 2026-03-07

### Fixed — Unfilled vertices after remap rebuild (3D stretching to origin)
- **Root cause A — Order-dependent fingerprint hash**: `MeshFingerprint.Compute` used sequential FNV-1a over positions/UV0, which depends on vertex order. Unity's FBX reimport can reorder vertices without changing geometry, causing ALL meshes to appear stale on every reimport. Changed to order-independent hash (XOR of per-vertex FNV hashes). Existing sidecars will show stale once (transition), then stable after re-Apply.
- **Root cause B — No used-tracking in `RebuildRemapFromPositions`**: When multiple raw vertices share the same position (UV seams, hard edges), all could match to the same stored vertex, leaving other optimized indices unfilled (zero position/normal/UV0 → 3D stretching). Added `usedStored[]` tracking with priority: unused+valid-remap > unused+invalid > used. Fixes 864/856/323 unfilled vertices on TrainСarriage LOD0/1/2.
- **Normal-based disambiguation**: Added `vertNormals` to sidecar (raw FBX normals). When UV0 alone can't distinguish candidates at the same position (hard edges: same pos+UV0, different normal), normal dot product breaks ties.
- **Fixed matched counter**: No longer double-counts vertices where `origRemap[j] == -1` (Pass 1 increments, Pass 2 re-processes since `newRemap[i] < 0`).

## [0.13.31] - 2026-03-07

### Fixed — Per-face composite UV2 for overlap groups spanning multiple sources
- On lower LODs, one target shell can span multiple source shells (e.g., belt: LOD0 has 13 source shells, LOD2 has 4 target shells covering the same UV0 area).
- Instead of picking one source for all vertices, each target face now gets UV2 from the source that is geometrically closest (per-face 3D proximity vote).
- Shared vertices at source boundaries are handled by last-writer-wins — lightmap padding covers micro-seams.
- Log shows `composite=Nsrc` when multiple sources contribute UV2 to a single target shell.
- All contributing sources are claimed to prevent reuse by other targets.

## [0.13.30] - 2026-03-07

### Fixed — Source claiming prevents same-source UV2 overlap in overlap groups
- **Source claiming**: track which source shells are already used by earlier targets (both interp and merged). Overlap unified ranking prefers unclaimed sources to prevent multiple targets from mapping to the same source, which produces UV2 overlap artifacts.
- **Ranking chain**: `issues → unclaimed → hint → vote winner → vote count → centroid distance`
- Log shows `CLAIMED` when the best available source was already taken by another target.
- Examples: LOD2 t81/t88 both picking src102, or t78/t99/t100 all picking src105 — now the second target is redirected to its next-best unclaimed source.

## [0.13.29] - 2026-03-07

### Fixed — Demote centroid distance to tiebreaker in overlap ranking
- Centroid distance was overriding face-proximity voting (e.g. LOD1 t106: 14/14 votes for src96 but centroid picked src104). A small nearby source shell can have a closer centroid while its individual faces are wrong.
- **New ranking chain**: `issues → hint → vote winner → vote count → centroid distance`
- Face-proximity voting is the most reliable geometric signal (compares individual faces). Cross-LOD hint provides consistency. Centroid distance and area ratio kept for diagnostics and final tiebreaker only.

## [0.13.28] - 2026-03-07

### Added — 3D centroid distance + area ratio diagnostic data
- Precompute per-source-shell 3D area and centroid distance for overlap groups.
- Log `dist3D` and `areaR` in overlap unified output for debugging.

### Fixed (0.13.27) — Deterministic overlap shell matching
- **Vote tie-breaking**: use 3D centroid distance as deterministic tiebreaker when vote counts are identical.
- **Deterministic iteration order**: sort Dictionary keys in dedup phase and overlap group members. Sort shell extraction by root face index for stable shell IDs.

## [0.13.11] - 2026-03-03

### Fixed — UV Preview uses the same checker as model with requested alpha
- In `UvTransferWindow.cs`, UV Preview background now uses the same checker texture source that is applied to the model and renders it with `alpha = 0.33333` when checker mode is enabled.
- Kept fallback checker path, but aligned checker-enabled fallback alpha to `0.33333` as well.
- Version format kept as `xx.xx.xx` (`0.13.11`).

## [0.13.10] - 2026-03-03

### Fixed — UV Preview background parity with model material/checker
- **UV Preview background source** (`UvTransferWindow.cs`): canvas now resolves a real background texture for preview instead of relying only on generic gray checker underlay.
  - If **Checker** mode is enabled, UV Preview uses the same procedural checker texture as Scene preview.
  - Otherwise it takes the first available `_MainTex` from preview LOD renderer materials.
- **Textured UV background draw pass** (`UvTransferWindow.cs`): added textured GL quad rendering for the 0-1 tile and neighboring UDIM tiles so the UV backdrop visually matches model shading reference.
- **Checker texture accessor** (`CheckerTexturePreview.cs`): added `GetCheckerTexture()` to share one checker source between Scene and UV Preview.
- **Editor resource lifecycle** (`UvTransferWindow.cs`): added creation/disposal of the texture drawing material (`texMat`) in `OnEnable` / `OnDisable`.

## [0.13.7] - 2026-03-03

### Fixed — UV0-interp fallback for broken force3D on thin wrapping geometry
- **Pass 2 UV0-only constrained fallback** (`GroupedShellTransfer.cs`): when both
  force3D passes (all-source and constrained) produce high issues (>50% of face
  count), a third pass runs UV0 interpolation constrained to the assigned source
  shell without the force3D override. On thin wrapping geometry (belts, straps,
  bands that wrap around a box), 3D projection maps vertices to scattered edge
  faces of the source, producing 100% face issues and garbage UV2 topology. UV0
  interpolation finds the correct source triangle in UV0 space and produces UV2
  matching the source LOD0 layout.
- **No overlap guard on Pass 2**: same-source overlap from UV0 interpolation is
  geometrically correct for tiling/symmetric geometry — both shells sample the
  same lightmap texels. The overlap guard is skipped to avoid rejecting valid
  candidates.
- **Conservative threshold**: >50% face issues triggers the fallback. Normal
  force3D shells get 0–2 issues; broken belt shells get 100%. Only
  catastrophically broken projections trigger the fallback.
- **Log label**: `UV0-interp fallback` distinguishes from `3D-primary` and
  `src-constrained` in diagnostics.

## [0.13.0] - 2026-03-03

### Changed — SymmetrySplit source-only default + sidecar metadata + validation improvement
- **SymmetrySplit restricted to source LOD by default** (`UvTransferWindow.cs`):
  `ExecSymmetrySplit` now accepts `bool includeTargets` parameter. By default
  only source LOD is processed, preventing accidental topology changes on target
  LODs that could degrade Transfer/Apply stability. UI toggle "SymSplit target
  LODs (advanced / risky)" enables target processing with a warning.
- **Symmetry split flag in sidecar** (`Uv2DataAsset.cs`, `Uv2AssetPostprocessor.cs`):
  new `stepSymmetrySplit` field in `MeshUv2Entry` (schema version bumped to 2).
  Sidecar now records whether symmetry split was applied, enabling deterministic
  replay on reimport. Backward-compatible: `ResolveSymmetrySplitStep()` infers
  the flag for old schema v0/v1 sidecars.
- **`wasSymmetrySplit` per-mesh tracking** (`UvTransferWindow.cs`): each
  `MeshEntry` tracks whether symmetry split modified it. Used in Apply to
  populate sidecar flags and build replay data.
- **Majority vote shell determination** (`TransferValidator.cs`): triangle
  shell assignment now uses 3-vertex majority vote instead of first-vertex
  lookup. If 2 of 3 vertices share a shell, that shell wins; otherwise falls
  back to first valid vertex. Improves stretch outlier detection accuracy for
  boundary triangles.
- **Transfer diagnostics**: source layout logging, vertex count mismatch
  warnings for previously transferred meshes, target LOD modification warnings
  when SymSplit was applied to targets.

## [0.11.3] - 2026-03-03

### Fixed — Cross-source UV2 overlaps from merged shells
- **Constrained search for force3D merged shells** (`GroupedShellTransfer.cs`):
  the 3D-primary merged mode (introduced in 0.11.1 for dedup conflicts) used
  all-source search, which could produce UV2 coordinates in other source
  shells' UV2 regions — overlapping with target shells assigned to those
  sources (e.g. `uv2sh[91](interp,src102) vs uv2sh[93](merged,src104):
  diff-src`). Now constrains force3D search to the assigned source shell's
  faces only, keeping UV2 within the source's UV2 bounds while still using
  3D-primary decision logic for vertex projection.
- **All-source overlap guard** (`GroupedShellTransfer.cs`): for non-force3D
  merged shells that fall back to all-source search, the candidate UV2 AABB
  is checked against claimed source shells' UV2 AABBs. If overlap is
  detected, the constrained result is preferred even with more triangle
  issues — trading minor quality loss for overlap-free lightmaps.
- Eliminates merged-involved diff-src overlaps reported by the validator.

## [0.11.2] - 2026-03-03

### Fixed — Cross-source UV2 overlaps from xform extrapolation
- **Xform bounds check** (`GroupedShellTransfer.cs`): the similarity transform
  (xform) method could extrapolate UV2 coordinates beyond the source shell's
  UV2 region, causing overlaps with shells from different sources (e.g.
  `uv2sh[11](xform,src11) vs uv2sh[109](interp,src117): diff-src`).
- Now precomputes UV2 bounding boxes for each source shell and applies a
  two-level penalty during xform vs interp method selection:
  - If xform output AABB crosses into another source shell's UV2 AABB,
    xform is invalidated entirely — forces interp (which stays within
    source UV2 convex hull by construction).
  - If OOB but no cross-shell overlap, applies mild penalty (OOB vertex count).
- Eliminates diff-src xform-involved overlaps reported by the validator.

## [0.11.1] - 2026-03-03

### Fixed — Same-source UV2 overlaps via 3D-primary merged mode
- **Dedup conflict resolution** (`GroupedShellTransfer.cs`): when multiple
  target shells claim the same source and no unique alternative exists
  (all would force merged mode), previous logic reverted to the overlapping
  source — causing identical UV2 positions and lightmap seams. Now forces
  conflicting shells into a new **"3D-primary" merged mode** that:
  - Skips the constrained pass (which would reproduce the overlap)
  - Uses all-source search with 3D projection as the primary method
  - 3D projection naturally maps each target to its spatially nearest
    source geometry, giving unique UV2 regions without overlap
- Eliminates same-source xform-involved overlaps reported by the validator
  (e.g. `uv2sh[93](xform,src104) vs uv2sh[99](xform,src104): SAME-SRC`).
- New log labels: `merged+3D` during dedup, `3D-primary` in per-shell
  diagnostics.

## [0.11.0] - 2026-03-03

### Added — Multi-criteria source shell rescoring for merged shells
- **RescoreMergedShells** (`GroupedShellTransfer.cs`): new Phase 2a+ step
  between initial shell matching and deduplication. When `DetectMergedShell`
  marks a target shell as merged (because 3D centroid-based matching picked
  a wrong source), the new step evaluates all source shells using a 4-criteria
  weighted score:
  - UV0 coverage fraction (35%) — direct measure of UV0 compatibility
  - Normal agreement (30%) — disambiguates front/back on thin geometry
  - UV0 area ratio (20%) — filters mismatched shell sizes
  - 3D centroid distance (15%) — spatial proximity prior
- If the best-scoring source has UV0 coverage >= 70%, the shell is un-merged
  and reassigned to the correct source, avoiding the lossy all-source fallback.
- **ComputeUv0CoverageFraction** helper: refactored continuous version of
  `DetectMergedShell` logic, returns 0..1 fraction instead of binary bool.
- Precomputed per-shell average face normals and total UV0 areas for both
  source and target shells (used by the scoring function).

## [0.9.94] - 2026-03-03

### Added — Edge analysis + spatial hash optimization (inspired by UnityMeshSimplifier)
- **EdgeAnalyzer** (`EdgeAnalyzer.cs`): new utility for edge-level mesh topology
  analysis. Builds edge-face adjacency via position-group spatial hashing,
  classifies each geometric edge as Border, Interior, UvSeam, UvFoldover,
  HardEdge, or NonManifold. Provides `FindHardEdgeVertices()` for the planned
  hard-edge shell splitting feature, and `FindBorderVertices()` for mesh
  boundary detection. Uses Union-Find on position groups similar to
  UnityMeshSimplifier's Smart Linking approach.
- **SourceGuidedWeld spatial hash** (`Uv0Analyzer.cs`): replaced O(n×m) brute
  force nearest-source-vertex lookup with 3D spatial hash grid (27-cell
  neighborhood search). Cell size auto-computed from source mesh AABB diagonal
  divided by cube root of vertex count. Falls back to brute force per-vertex
  on grid miss. Typical speedup ~10-50× for large meshes.
- **Seam/foldover weld distinction** (`Uv0Analyzer.cs`): SourceGuidedWeld now
  classifies vertex pairs as foldover (same UV0 → weld unconditionally) or
  seam (different UV0 → require same source shell). Foldover pairs no longer
  need the expensive source shell lookup. Logs `foldover:N seam:N` counts.

## [0.9.93] - 2026-03-02

### Added — Consistency check + texel density metric (inspired by CurioMesh)
- **Merged shell consistency check** in `GroupedShellTransfer`: for merged
  shells (multi-source), UV0 projection remains primary but now runs a
  secondary 3D surface projection with backface rejection (dot > 0.3).
  If UV0 hit is distant (sqr > 0.05) and UV2 results disagree (delta > 0.02),
  prefers the 3D result. Logs `consistency-corrected` vertex count.
  Closes the gap where merged shell UV0-only search could silently pick
  wrong triangles from distant UV0 regions.
- **Texel density metric** in `TransferValidator`: computes per-triangle
  `areaWorld / areaUV2` ratio, finds global median, flags triangles where
  ratio deviates > 200× from median as `TexelDensity` issue. New fields:
  `texelDensityRatios[]`, `texelDensityMedian`, `texelDensityBadCount`.
  Visible in Review tab as cyan "Txl" bar and fill color.
- **Backface threshold** (0.3) used in merged shell 3D fallback path,
  filtering source triangles whose normal opposes target vertex normal.
  Previously only used in legacy transfer pipeline.

## [0.9.74] - 2026-03-02

### Fixed — Interpolation primary, transform fallback only
- **Reverted similarity transform from primary to fallback.** Transform
  extrapolates when target UV0 differs from source UV0 (always on LOD meshes),
  causing inter-shell UV2 overlaps. Interpolation is bounded by source UV2
  triangle convex hull and cannot extrapolate.
- Transform now wins only when it has strictly fewer inverted/zero-area
  triangles than interpolation (was: wins on equal).
- Merged shells reverted to per-vertex UV0 interpolation across all source
  triangles (was: per-vertex transform via source shell assignment).

## [0.9.73] - 2026-03-02

### Changed — Similarity transform per-shell (primary transfer method)
- Added ComputeSimilarityTransform: least-squares UV0→UV2 fit per source shell.
- Per-shell transform precomputation with mirrored shell detection.
- Diagnostic logging: xform/interp/merged counts per mesh.
- New TransferResult fields: shellsTransform, shellsInterpolation, shellsMerged.

## [0.7.6] - 2026-03-01

### Added — Reset Working Copies button
- **Reset All Working Copies** button in Setup tab: visible when any meshes
  are modified (welded, repacked, or transferred). Restores all `originalMesh`
  back to FBX originals, clears all derived meshes and caches. Useful when
  re-selecting a previously processed LODGroup.
- **[W] badge** on welded meshes in the mesh list.

## [0.8.3] - 2026-03-01

### Changed — UV0-first transfer with 3D guard (UV Weld Selected philosophy)
- **UV0 is primary search space, 3D is only disambiguation guard.**
  For each target vertex: find nearest source triangle in UV0 2D space.
  If only one candidate → use directly. If multiple at same UV0 distance
  (overlapping UV shells from tiling textures) → use 3D position + normal
  to pick correct triangle. Compute UV0 barycentric → interpolate UV2.
- No shell assignment step needed. UV0 lookup naturally finds correct
  triangle; 3D only resolves ambiguity from overlapping shells.
- Logs "3D-disambiguated" count for vertices that needed overlap resolution.

## [0.8.2] - 2026-03-01

### Changed — Two-phase transfer: shell assignment + UV0-space projection
- **Phase 1: Shell assignment** — for each target vertex, find nearest source
  vertex by 3D distance with normal filter (dot > 0.3). This determines which
  source UV0 shell the target vertex belongs to. Normal filter separates
  front/back of thin walls.
- **Phase 2: UV0-space transfer** — within the assigned shell, find nearest
  source TRIANGLE in UV0 SPACE (not 3D!), compute 2D barycentric coordinates
  on the UV0 triangle, interpolate UV2. This is equivalent to 3ds Max
  "UV Weld Selected" — the actual UV transfer happens in UV space, eliminating
  all thin-wall / overlapping geometry issues.
- Shell identity comes from 3D geometry; UV2 values come from UV0 space.

## [0.8.1] - 2026-03-01

### Changed — Triangle surface projection transfer (replaces vertex-based)
- **Completely replaced vertex-based nearest matching with triangle projection**.
  For each target vertex: find nearest source TRIANGLE by point-to-triangle
  distance (filtered by face normal dot > 0.3), compute barycentric coordinates,
  interpolate UV2 from the 3 source triangle vertices.
- Triangle is atomic — all 3 vertices belong to same UV2 shell, eliminating
  seam ambiguity that plagued all vertex-based approaches.
- Includes full ClosestPointOnTriangle implementation (Ericson algorithm)
  with proper edge/vertex clamping for barycentric coords.
- UvTransferWindow now calls Transfer(targetMesh, sourceMesh) instead of
  Transfer(targetMesh, sourceShellInfos).

## [0.8.0] - 2026-03-01

### Fixed — 3-pass transfer: normal → 3D → UV0 disambiguation
- **Core insight**: one 3D vertex has multiple UV vertices on different shells
  (seam duplicates). Pure 3D nearest picks arbitrary seam duplicate → wrong UV2.
- **New 3-pass algorithm**:
  1. Normal filter (dot > 0.5) — separates front/back of thin walls
  2. 3D nearest among normal-compatible — spatial correspondence
  3. UV0 disambiguation — among source verts within epsilon of best 3D
     distance, pick the one whose UV0 is closest to target vertex's UV0.
     This correctly resolves seam vertices that share position+normal
     but belong to different UV shells with different UV2 coordinates.
- Logs UV0-disambiguated count for diagnostics.

## [0.7.9] - 2026-03-01

### Fixed — Normal-filtered 3D transfer (overlapping UV0 disambiguation)
- **Transfer uses normal filter + 3D nearest** instead of UV0 nearest.
  UV0 is overlapping/tiling on CementWall (front/back share same UV0),
  so UV0 nearest picks wrong matches. Normal filter (dot > 0.5) separates
  wall sides, then 3D nearest among filtered set gives unambiguous match.
  Falls back to unfiltered 3D nearest if no normal-compatible source found.

## [0.7.8] - 2026-03-01

### Fixed — Normal-filtered UV0 transfer (thin wall disambiguation)
- **Transfer uses normal filter + UV0 nearest**: for each target vertex,
  first filters source vertices by normal similarity (dot > 0.5), then
  finds nearest by UV0 among filtered set. Normal separates front/back
  of thin walls. UV0 gives precise position within same geometric side.
  Falls back to unfiltered UV0 nearest if no normal-compatible source found.

## [0.7.7] - 2026-03-01

### Fixed — Correct matching spaces for weld vs transfer
- **SourceGuidedWeld back to 3D nearest**: seam vertices have different UV0
  by definition, so UV0 matching gives them different source shell IDs →
  almost nothing gets welded. 3D matching works because normal check already
  prevents thin-wall confusion. Restores 26→18 shell reduction on CementWall.
- **Transfer stays UV0 nearest**: prevents copying UV2 from wrong side of
  thin geometry.

## [0.7.6] - 2026-03-01

### Fixed — UV0-space matching instead of 3D
- **Transfer uses UV0 nearest-vertex**: finds nearest source vertex by UV0
  distance, not 3D position. 3D matching fails on thin geometry (CementWall)
  where front/back faces are close in 3D but on different UV0 shells.
- **SourceGuidedWeld uses UV0 nearest**: determines source shell membership
  via UV0 proximity instead of 3D proximity. Same thin-geometry fix.

## [0.7.5] - 2026-03-01

### Fixed — Weld button runs source-guided weld
- **Weld button now two-phase**: Phase 1 = false-seam weld (pos+uv0+normal)
  for all meshes. Phase 2 = source-guided weld for target LODs — merges
  vertices by pos+normal when both belong to same source UV0 shell.
  Previously source-guided weld was hidden inside Transfer step only.
- **SourceGuidedWeld returns original** when nothing to weld (avoids
  unnecessary mesh copies).

## [0.7.4] - 2026-03-01

### Changed — Source-guided weld + 3D nearest-vertex transfer
- **Source-guided weld** (`Uv0Analyzer.SourceGuidedWeld`): merges target LOD
  vertices by position+normal only when both map to the same source UV0 shell
  via 3D proximity. Reunifies UV0 islands that LOD decimation split apart,
  while preserving intentional seams between different source shells.
  Fixes shell count mismatch (e.g. 7 source vs 26 target on CementWall).
- **3D nearest-vertex transfer**: completely replaces similarity-transform
  pipeline. For each target vertex, finds nearest source vertex by 3D position
  and copies UV2 directly. No UV0 shell matching dependency, no transforms.
  Eliminates UV2 out-of-bounds artifacts from degenerate/mirrored shells.
- **UI labels updated**: transfer report shows "3D nearest-vertex" method and
  "source shells used" count instead of matched/unmatched/mirrored.

## [0.7.3] - 2026-03-01

### Fixed — Mirrored shell transfer + degenerate shell fallback
- **Mirrored shells use source transform directly**: nearest-vertex matching
  gives wrong correspondences when target UV0 is reflected vs source. Now
  mirrored shells use precomputed `mirrorTransform` from source analysis.
- **Residual-based fallback**: if per-target-shell similarity transform has
  residual > 0.001 (degenerate small fragments), falls back to source's
  precomputed transform. Prevents tiny shells from producing wildly off UV2.
- **UV2 bounds diagnostic**: transfer now logs warning with exact bounds when
  any vertex UV2 falls outside 0-1 range.

## [0.7.2] - 2026-02-28

### Fixed — UV2 out-of-bounds on LODs + checker on all tabs
- **Per-target-shell nearest-vertex matching**: transfer no longer reuses source's
  precomputed UV0→UV2 transform directly on target UV0. Instead, for each matched
  target shell, finds nearest source vertex by UV0 distance and builds point pairs
  (target_UV0, nearest_source_UV2) to compute a fresh similarity transform per shell.
  Fixes UV2 coordinates going far outside 0-1 when LOD UV0 layout differs from LOD0.
- **Checker in toolbar**: moved checker toggle from Review tab to canvas toolbar,
  accessible on any tab (Setup, Repack, Transfer, Review).

## [0.7.1] - 2026-02-28

### Fixed — Weld persistence + checker after Apply
- **Weld persists through FBX reimport**: sidecar now stores `welded` flag per mesh;
  postprocessor calls `WeldInPlace()` before UV2 injection so vertex count matches.
- **FBX path lookup after weld**: added `fbxMesh` reference to MeshEntry so
  `AssetDatabase.GetAssetPath` works even after `originalMesh` replaced by welded copy.
  Previously welded meshes were silently skipped during Apply.
- **Checker preview after Apply**: `ToggleChecker` now falls back to `originalMesh`/`fbxMesh`
  if working copies were cleared by Refresh, checking for existing UV2 channel.

## [0.7.0] - 2026-02-28

### Added — UV0 analysis/fix + split padding
- **UV0 Analyzer**: detects false UV seams (weld candidates), degenerate UV triangles,
  flipped UV triangles within shells, and overlapping shell groups. Report displayed
  in Setup tab with per-mesh breakdown and color-coded warnings.
- **UV0 Weld**: merges false-seam vertices (identical position + UV0 + normal but
  different indices) by rebuilding index buffer and compacting vertex arrays.
  All vertex attributes preserved (tangents, UV1, colors, bone weights, submeshes).
  Operates on working copies only — FBX untouched.
- **Split padding**: shell padding (inter-island, passed to xatlas) and border padding
  (atlas edges, applied as post-repack linear inset). Border padding defaults to 0
  for Clamp mode lightmaps. Two separate sliders in Repack tab.
- `Uv0Analyzer.cs` — analysis + weld pipeline with spatial hashing for O(n) duplicate detection
- `RepackOptions.borderPadding` field, `XatlasRepack.ApplyBorderInset()` post-process

## [0.6.0] - 2026-02-28

### Added — Checker preview, FBX postprocessor, UV2 reset
- **Checker 3D preview**: procedural 8×8 colored grid with alphanumeric labels (A1..H8),
  applied to model in SceneView via UV2 channel. Temporarily swaps materials + meshes;
  fully restored on disable. Button in Review tab.
- **Apply UV2 to FBX (postprocessor)**: saves UV2 as sidecar ScriptableObject
  (`ModelName_uv2data.asset`) beside the FBX. `Uv2AssetPostprocessor.OnPostprocessModel`
  injects UV2 on every FBX reimport — identical to Unity's "Generate Lightmap UVs" approach.
  FBX file stays untouched on disk.
- **Reset UV2**: deletes sidecar asset and reimports FBX, removing all transferred UV2.
- `CheckerTexturePreview.cs` — procedural texture generation + material/mesh swap management
- `Uv2DataAsset.cs` — sidecar ScriptableObject storing per-mesh UV2 arrays
- `Uv2AssetPostprocessor.cs` — AssetPostprocessor that reads sidecar and injects UV2
- `CheckerUV2.shader` reads TEXCOORD2 for UV2 visualization

## [0.5.0] - 2026-02-28

### Changed — Architecture pivot: UV0-space shell transforms replace 3D projection
- **New transfer approach**: instead of projecting target vertices onto source mesh in 3D,
  compute per-shell similarity transform (rotate + scale + translate) from LOD0's UV0→UV2
  mapping, then apply the same transform to all LOD vertices via UV0 shell matching.
- xatlas repack preserves shell internal structure — only changes placement. This means
  the UV0→UV2 mapping per shell is a pure similarity transform (4 parameters: a, b, tx, ty).
- Shell matching uses UV0 bounding box overlap + UV0 centroid distance + 3D centroid
  proximity (disambiguates stacked/mirrored shells).
- Transfer is mathematically exact — no interpolation, no barycentric projection, no artifacts.
- Old 3D projection pipeline (SourceMeshAnalyzer, ShellAssignmentSolver, InitialUvTransferSolver,
  BorderRepairSolver) bypassed; code retained for reference.

### Added
- `GroupedShellTransfer.cs` — complete new pipeline: AnalyzeSource + Transfer
- `GroupedShellTransfer.ShellTransform` — similarity transform struct with Apply method
- `GroupedShellTransfer.SourceShellInfo` — per-shell data for cross-LOD matching
- `GroupedShellTransfer.ComputeSimilarityTransform` — least-squares similarity fit (UV0→UV2)
- Shell transform cache in UvTransferWindow (avoids re-analysis on repeated transfers)
- Review tab shows shells matched/unmatched + vertex coverage instead of triangle status bars

## [0.4.1] - 2026-02-28

### Changed
- BVH vertex projection is now primary UV transfer method (was fallback)
- Face-level bindings demoted to fallback when BVH finds no shell match
- Raised BVH projection weights (0.9 direct hit, 0.6 shell scan) to reflect higher reliability

## [0.4.0] - 2026-02-28

### Fixed
- Critical: vertex UV averaging across different shells producing stretched triangles spanning entire atlas
- Per-shell vertex accumulator now isolates UV contributions by shell ID — no cross-shell blending
- UV0 proximity used as priority signal for shell conflict resolution at shared vertices
- Post-validation pass detects and re-projects anomalous triangles exceeding shell UV bounds

### Added
- Target UV0 loaded in PrepareTarget for shell priority matching
- Source UV0 interpolation in InterpolateVertexUv and FallbackVertexProject
- Debug logging for vertex conflict resolution statistics

## [0.3.4] - 2026-02-28

### Fixed
- Repack now packs all selected meshes into a single shared UV atlas instead of repacking each mesh independently, preventing UV2 overlap when meshes share the same lightmap

## [0.1.0] - 2026-02-28

### Added
- xatlas native bridge (C++ DLL) with repack-only UV packing
- UV shell extraction via Union-Find connectivity
- UV overlap classifier with chart instance generation
- Full 7-stage UV transfer pipeline (shell assignment → initial transfer → border repair → validation)
- Triangle BVH for fast surface projection
- Border primitive detection and UV perimeter metrics
- Border repair solver with quality gate and conditional fuse
- Transfer quality evaluator with triangle status classification
- Editor Window with 8-stage pipeline control and per-stage re-run
- UV preview canvas with 7 visualization modes
- Multi-mesh atlas support (multiple renderers per LOD)
- Per-mesh quality reports
- CMake build system for native DLL (auto-fetches xatlas)
