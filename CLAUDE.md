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

## Code Rules

- Namespace: `LightmapUvTool`
- No `using System.Text.RegularExpressions` in `LightmapTransferTool.cs` — use fully qualified `System.Text.RegularExpressions.Regex`
- `internal` visibility for cross-tool helpers (same assembly)
- `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate` for all scene modifications
- Logging via `UvtLog.Info()` / `UvtLog.Warn()` / `UvtLog.Error()` (prefixed `[LightmapUV]`)
- FBX code gated by `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER`
- `RestoreWorkingMeshes()` before clearing/switching LODGroup context
- Destroy temporary meshes (repacked, transferred, welded) when no longer needed

## Architecture Quick Reference

- **Entry point:** `Editor/Framework/UvToolHub.cs` — main EditorWindow
- **Context:** `Editor/Framework/UvToolContext.cs` — shared state
- **Tools:** `Editor/Tools/` — each implements `IUvTool`
- **Native:** `Plugins/` binaries, `Native/` C++ source
- **Sidecar:** `Uv2DataAsset` persists UV2/collision data alongside FBX

## Key Patterns

- LOD siblings: `baseName[_-\s]LOD{N}` regex, case-insensitive
- Mesh group key: `UvToolContext.ExtractGroupKey()` strips LOD/COL suffixes
- FBX export: clone prefab → replace meshes → add LOD/COL → `ModelExporter.ExportObjects`
- Sidecar: generate → save to `_uv2data.asset` → export to FBX (non-destructive)
