---
name: repo-conventions
description: Canonical conventions for THIS repository (UnityMeshLab) — the package ID, namespace, Unity version target, CI workflow names, and any deviations from _shared/naming-conventions.md. Use at the start of any non-trivial task in this repo, when creating new C# files (to pick the correct namespace), when adding asmdefs, or when editing package.json. Overrides _shared/* where they conflict.
---

# repo-conventions (UnityMeshLab)

Canonical conventions for the `UnityMeshLab` repository. This file overrides `_shared/naming-conventions.md` where an explicit deviation is documented. Read this first when creating new files, adding asmdefs, or editing `package.json`.

## Identity (canonical target)

| Field | Canonical value |
|---|---|
| Repository | `SashaRX/UnityMeshLab` |
| Display name | `Mesh Lab` |
| Package ID | `com.sasharx.unitymeshlab` |
| Root namespace | `SashaRX.UnityMeshLab` |
| Unity minimum version | `6000.0` (Unity 6) |
| Default branch | `main` |

## Assemblies

| Role | Asmdef name | `rootNamespace` | Platforms |
|---|---|---|---|
| Runtime | `SashaRX.UnityMeshLab` | `SashaRX.UnityMeshLab` | all |
| Editor | `SashaRX.UnityMeshLab.Editor` | `SashaRX.UnityMeshLab.Editor` | `Editor` only |
| Tests (Editor) | `SashaRX.UnityMeshLab.Tests.Editor` | `SashaRX.UnityMeshLab.Tests` | `Editor` only |

## CI workflows

Located under `.github/workflows/`:

- `build-native.yml` — builds native plugin binaries (platform-specific).
- `meta-check.yml` — verifies `.meta` file coverage.
- `version-bump.yml` — automates `package.json` version bumps.

Missing and planned (see `unity-ci-validation/SKILL.md`):

- `test.yml` — EditMode/PlayMode matrix on 6000.0 minimum.
- `release.yml` — tag-triggered GitHub Release.

## Deviations from `_shared/naming-conventions.md`

Both items below are known deviations captured for tracking; the migration to the canonical values is planned (see "Migration status" below).

- **`package.json` `name`** is currently `com.sasharx.lightmap-uv-tool` — does NOT match the repository folder name `UnityMeshLab`. Canonical value: `com.sasharx.unitymeshlab`. Rationale for deviation: historical — the repo originated as `lightmap-uv-tool` before the mesh-lab rename. Migration breaks downstream consumers; schedule under a MAJOR SemVer bump.
- *(Resolved in the `claude/skills-overhaul-phase-0-Xrg7K` branch: namespace migrated from bare `LightmapUvTool` to `SashaRX.UnityMeshLab` across 48 `.cs` files; asmdef renamed `LightmapUvTool.Editor.asmdef` → `SashaRX.UnityMeshLab.Editor.asmdef`, GUID preserved.)*

## Primary domain vocabulary

Terms that identify tasks as in-scope for this repo (used as description triggers elsewhere):

- Mesh editor, mesh hygiene, mesh repacking.
- Lightmap UV, UV2, baked lightmap, UV transfer.
- LOD group, LOD UV workflow, LOD sibling detection.
- FBX export (gated by `LIGHTMAP_UV_TOOL_FBX_EXPORTER`).
- Sidecar asset (`Uv2DataAsset` — persists UV2/collision data next to FBX).

## Repo-specific rules (from CLAUDE.md)

Shared with agents via `CLAUDE.md`:

- No `using System.Text.RegularExpressions` in `LightmapTransferTool.cs` — use fully qualified `System.Text.RegularExpressions.Regex`.
- Log via `UvtLog.Info` / `UvtLog.Warn` / `UvtLog.Error` — prefixed `[LightmapUV]`.
- Use `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate` for scene modifications.
- Call `RestoreWorkingMeshes()` before clearing/switching LODGroup context.
- Destroy temporary meshes (repacked, transferred, welded) when no longer needed.

## Migration status

Namespace migration to `SashaRX.UnityMeshLab` is complete. The `package.json` `name` rename from `com.sasharx.lightmap-uv-tool` to `com.sasharx.unitymeshlab` remains scheduled — it is a downstream-breaking change and must ship under a MAJOR SemVer bump with explicit user communication.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `migration-and-refactor-planner/SKILL.md`
- `unity-package-architect/SKILL.md`
- `unity-ci-validation/SKILL.md`
