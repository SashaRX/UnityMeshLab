# AI Agent Rules — Unity Package (com.sasharx.lightmap-uv-tool)

Shared rules for **all AI agents** (Codex, Claude, etc.) working on this repository.

## Package Structure & Zones

| Zone | Purpose | Sensitivity |
|------|---------|-------------|
| `Editor/` | Editor-only tools (never ships in builds) | High — asmdef, Undo, lifecycle |
| `Plugins/` | Native binaries (xatlas, V-HACD, meshoptimizer) | Critical — binary must match `Native/` source |
| `Native/` | C/C++ source for native plugins | Critical — changes trigger CI rebuild |
| `Shaders/` | Compute/render shaders for GPU tools | Medium — platform compatibility |
| `package.json` | UPM manifest | High — version, dependencies |
| `CHANGELOG.md` | Release notes | Low — documentation |
| `README.md` | User-facing docs | Low — documentation |

## Hard Rules

### Meta files
- Every file and directory MUST have a `.meta` file
- NEVER delete, regenerate, or modify GUIDs in `.meta` files
- Do NOT create `.meta` manually — Unity generates them
- Do NOT commit bulk `.meta` changes unless files were actually added/removed

### Assembly & platform
- All Editor code under `Editor/` with `SashaRX.UnityMeshLab.Editor.asmdef`
- `includePlatforms: ["Editor"]` — never leak into runtime builds
- Do NOT mix Runtime and Editor dependencies
- FBX exporter code gated by `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER`

### Package integrity
- Do NOT change `package.json` name/displayName without explicit request
- Do NOT break public API without clear justification and changelog entry
- Do NOT modify native binaries in `Plugins/` directly — rebuild from `Native/` source
- Define symbols (`versionDefines`) must match actual package dependencies

### Transfer Pipeline
- Before modifying GroupedShellTransfer, XatlasRepack, or SymmetrySplitShells — read `EXPERIMENTS.md`
- Each experiment = 1 small PR, 1 concern, testable on simple model first
- Document result in `EXPERIMENTS.md` before merging

### Code conventions
- Namespace: `SashaRX.UnityMeshLab`
- No `using System.Text.RegularExpressions` in `LightmapTransferTool.cs` — use fully qualified path
- `internal` visibility for cross-tool helpers (same assembly)
- All scene modifications via `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate`
- Logging via `UvtLog.Info()` / `UvtLog.Warn()` / `UvtLog.Error()`

## Review Focus (Critical Issues)

For review, these are the **actually important** things to catch in this package:

1. **API breaks** — public method signature changes, removed types, renamed serialized fields
2. **GC spikes** — allocations in `OnGUI`, `Update`, `OnSceneGUI` hot paths
3. **Editor/Runtime leakage** — runtime code referencing `UnityEditor`, or editor code missing platform guards
4. **Serialization issues** — changed `[Serializable]` field types/names break existing sidecar assets
5. **Domain reload** — static state that survives assembly reload without cleanup
6. **asmdef dependencies** — missing references, circular deps, wrong platform filters
7. **Mesh/Object lifecycle** — temporary meshes not destroyed, MeshFilter.sharedMesh not restored
8. **LODGroup lifecycle** — `RestoreWorkingMeshes()` before clearing/switching context
9. **Native plugin ABI** — C# marshalling must match C++ signatures exactly
10. **Undo support** — all scene modifications must be undoable

## Naming Conventions

- LOD objects: `Name_LOD{N}` (e.g., `Chair_LOD0`, `Chair_LOD1`)
- Collision objects: `Name_COL` (simplified) or `Name_COL_Hull{N}` (convex decomp)
- Mesh group key: `UvToolContext.ExtractGroupKey()` strips LOD/COL suffixes
- Sidecar assets: `ModelName_uv2data.asset`
