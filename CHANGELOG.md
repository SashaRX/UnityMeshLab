# Changelog

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
