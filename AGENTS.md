# AI Agent Rules — UnityMeshLab

Shared rules for all AI agents (Codex, Claude, etc.) working on this repository.

The canonical rule set lives in `.claude/skills/`. This file is the top-level pointer: start here, then follow skill references. Per-repo deviations and identity (package ID, namespace, Unity minimum, CI workflows) are documented in `.claude/skills/repo-conventions/SKILL.md`.

## Entry points

- **Repo identity, namespaces, CI workflows** — `.claude/skills/repo-conventions/SKILL.md`.
- **Cross-repo conventions (package layout, naming, version gates, anti-patterns)** — `.claude/skills/_shared/`.
- **Verification checklists** (undo, prefab, batch, release) — `.claude/skills/_checklists/`.
- **Skill catalog** — every skill in `.claude/skills/*/SKILL.md` has scope, delegations, and canonical patterns.

## Package-specific zones

| Zone | Purpose | Sensitivity |
|------|---------|-------------|
| `Editor/` | Editor-only tools | High — asmdef, Undo, lifecycle |
| `Plugins/` | Native binaries (xatlas, V-HACD, meshoptimizer) | Critical — must match `Native/` source |
| `Native/` | C/C++ source for native plugins | Critical — changes trigger CI rebuild |
| `Shaders/` | Compute/render shaders for GPU tools | Medium — platform compatibility |
| `package.json` | UPM manifest | High — version, dependencies |

## Domain-specific hard rules (not covered by skills)

- **Native plugins**: never modify `Plugins/*.dll|*.so|*.dylib|*.bundle` directly — rebuild from `Native/` source via the `build-native.yml` CI workflow.
- **Transfer pipeline experiments**: read `EXPERIMENTS.md` before modifying `GroupedShellTransfer`, `XatlasRepack`, or `SymmetrySplitShells`. One experiment per PR, documented in `EXPERIMENTS.md`.
- **LODGroup lifecycle**: call `RestoreWorkingMeshes()` before clearing or switching LODGroup context.
- **LOD / collision naming**: `Name_LOD{N}` (e.g., `Chair_LOD0`), `Name_COL` or `Name_COL_Hull{N}`. Group key extracted via `UvToolContext.ExtractGroupKey()`.
- **Sidecar assets**: `ModelName_uv2data.asset` — persists UV2/collision data alongside FBX.
- **FBX exporter**: code gated by `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER`.
- **Regex in `LightmapTransferTool.cs`**: use fully-qualified `System.Text.RegularExpressions.Regex` — no top-level `using`.
- **Logging**: `UvtLog.Info` / `UvtLog.Warn` / `UvtLog.Error` (prefix `[LightmapUV]`).

For mutation safety, package structure, serialization, CI, and release mechanics — consult the relevant skill in `.claude/skills/`, not this file.

## Review focus

For review, the actually-important things to catch in this package:

1. **API breaks** — public method signatures, removed types, renamed serialized fields.
2. **Editor/Runtime leakage** — runtime code referencing `UnityEditor`; editor code missing platform guards.
3. **Serialization** — changed `[Serializable]` field types/names break existing sidecar assets.
4. **Domain reload** — static state that survives assembly reload without cleanup.
5. **Native plugin ABI** — C# marshalling must match C++ signatures exactly.
6. **LODGroup lifecycle** — `RestoreWorkingMeshes()` ordering.
7. **Mesh lifecycle** — temporary meshes destroyed, `MeshFilter.sharedMesh` restored.
8. **Undo coverage** — scene modifications must be undoable (see `unity-undo-prefab-safety`).

Mutation-safety, batching, prefab, serialization, and package rules are enforced through the skills in `.claude/skills/`; the reviewer skill (`unity-package-reviewer`) and the auditor skill (`repo-auditor`) cite line-level violations.
