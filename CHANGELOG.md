# Changelog

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
