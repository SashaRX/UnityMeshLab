# Claude — Executor Mode

Claude's role in this repo: **write code, fix bugs, implement features, fix CI**.
Claude does NOT do final review — that's Codex's job.

See `AGENTS.md` for shared rules that apply to all AI agents.

## Workflow

1. Before changes — short plan (what, where, why)
2. Small, focused changes — one concern per commit
3. Do NOT touch unrelated files
4. Do NOT bulk-rename/reformat unless explicitly asked
5. Verify compile locally before proposing PR
6. Split large tasks into small PRs
7. Before experimenting with transfer pipeline — read `Documentation~/EXPERIMENTS.md`

## Code Rules

- Namespace: `SashaRX.UnityMeshLab`
- No `using System.Text.RegularExpressions` in `LightmapTransferTool.cs` — use fully qualified `System.Text.RegularExpressions.Regex`
- `internal` visibility for cross-tool helpers (same assembly)
- `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate` for all scene modifications
- Logging via `UvtLog.Info()` / `UvtLog.Warn()` / `UvtLog.Error()` (prefixed `[MeshLab]`)
- FBX code gated by `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER`
- `RestoreWorkingMeshes()` before clearing/switching LODGroup context
- Destroy temporary meshes (repacked, transferred, welded) when no longer needed

## Architecture Quick Reference

- **Entry point:** `Editor/Framework/UvToolHub.cs` — main EditorWindow
- **Context:** `Editor/Framework/UvToolContext.cs` — shared state
- **Tools:** `Editor/Tools/` — each implements `IUvTool`
- **Geometry:** `Editor/Geometry/` — `MeshGeometry` (face normals, welding, sample directions, barycentrics) and `GpuReadback` (the one GPU → CPU texture read); spatial queries in `Editor/TriangleBvh.cs` / `TriangleBvh2D.cs`. Use these; never add a tool-local copy
- **LOD hierarchy:** `Editor/Mesh/LodHierarchy.cs` — a LODGroup from its hierarchy (`CollectByName`, `LodsFromNames`, `RebuildFromNames`, `CreateFromRenderers`, `CreateFromSiblings`, `Compact`, `RootRendererIsLod0`, `MoveRootMeshToChild`, `SortChildrenAsLods`, `AddColliderFromCollisionMesh`, `AssignCollider`); `LodGroupUtility` is the component half (recreate, apply, transition normalisation)
- **Vertex channels:** `Editor/Mesh/VertexChannels.cs` — a scalar per-vertex channel (`AOTargetChannel`: colour component or UV component) decoded, read, written (whole mesh or one submesh), levels/blend, greyscale preview colours, fill/snapshot/restore of `colors32`. Never decode the enum or poke `colors32`/`GetUVs` for a scalar channel in a tool
- **UV:** `Editor/Uv/UvTopology.cs` — index-space UV layout topology (channel read, boundary edges, unique edges, point-in-triangle, shell vote, face→shell, shell data, UDIM tiles); the canvas, 3D layer and viewport cache its results, never recompute them locally
- **Bench:** `Editor/Bench/` — `SweepRunner` (matrix → cells → pipeline runs through `ISweepHost`), `BenchmarkRunner` (suite cases → spawn → techniques, through `IBenchmarkHost`), `BenchmarkRecorder`, `BenchmarkSweep` (aggregate, winner, gallery, manifest, archive), `TestSuiteAsset`, `CsvUtil`. A tab implements the host interfaces; it never loops over cells itself
- **Diagnostics:** `Editor/Diagnostics/` — `DebugUi` (the one Show Debug UI gate `Enabled`, the DEBUG banner, the log-filter block; `IUvToolDebugOnly` marks a tab the hub hides while the setting is off), `BakeHealth` (a Remesh & Bake node's health as a report: summary, scale check, warnings), `HierarchicalDiag`, `FbxMetricsExporter`. The Diagnostics tab (`Editor/Tools/DiagnosticsTool.cs`) hosts the sweep, the benchmark, the log filters and the reports; never read `showDebugUI` or draw a debug banner in a tool
- **Assets:** `Editor/Assets/` — `TextureAssets` (a texture from pixels, PNG/EXR encoding, file writes, image read-back, map importer configuration), `FbxExport` (every FBX write: the isolated channel re-save, the LOD-rebuild hierarchy passes, the atomic write, the post-reimport relink; UV / vertex-colour saves are delegated to `FbxChannelWrite`, which edits the FBX document through the FBX SDK via `FbxDocumentEdit` and pairs corners with vertices via `FbxCornerMatch` — never re-export a file to change a channel) and `SidecarStore` (the `_uv2data.asset` sidecar: UV2 entries, collision hulls, tool settings). Tools never call `ModelExporter` or open a `Uv2DataAsset` themselves
- **Native:** `Plugins/` binaries, `Native~/` C++ source
- **Sidecar:** `Uv2DataAsset` persists UV2/collision data alongside FBX

## Key Patterns

- Names: `Editor/Mesh/MeshNaming.cs` is the only reader of the naming rules — LOD suffix `baseName[_-\s]LOD{N}` (case-insensitive, index < 8), collision suffixes (`_COL`, `_COL_Hull{N}`, `_Collider`, `_Collision`), pipeline suffixes, group key. Never write a LOD/COL regex in a tool
- Mesh group key: `MeshNaming.GroupKey()` (forwarded by `UvToolContext.ExtractGroupKey()`) strips LOD/COL suffixes
- FBX export: clone prefab → replace meshes → add LOD/COL → `ModelExporter.ExportObjects`
- Sidecar: generate → save to `_uv2data.asset` → export to FBX (non-destructive)
