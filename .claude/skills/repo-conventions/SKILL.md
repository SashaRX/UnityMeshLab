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
- `test.yml` — EditMode test run on Unity 6000.0. License-gated: skips cleanly when no `UNITY_LICENSE`/`UNITY_SERIAL` secrets are configured. **This repository is on Unity Personal (free) tier**, and Unity disabled manual `.alf`→`.ulf` activation for Personal seats in 2024, so the test job is currently always **skipped** on GitHub-hosted runners. Local Test Runner remains the canonical pre-commit verification path. See `unity-ci-validation/SKILL.md` §License activation for the recipe and the path forward (self-hosted runner or Plus/Pro upgrade).
- `release.yml` — tag-triggered GitHub Release; verifies `v<version>` tag matches `package.json` and extracts the matching section from `CHANGELOG.md`.

## Deviations from `_shared/naming-conventions.md`

None at the canonical-target level. Both historical deviations (bare `LightmapUvTool` namespace, `com.sasharx.lightmap-uv-tool` package id) were resolved in the 1.0.0 release.

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

## Migration history

- **1.0.0 (2026-04-20)** — package id renamed `com.sasharx.lightmap-uv-tool` → `com.sasharx.unitymeshlab`; namespace renamed `LightmapUvTool` → `SashaRX.UnityMeshLab`; repository URL corrected to `UnityMeshLab.git`. Downstream migration steps in `CHANGELOG.md`.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `migration-and-refactor-planner/SKILL.md`
- `unity-package-architect/SKILL.md`
- `unity-ci-validation/SKILL.md`
