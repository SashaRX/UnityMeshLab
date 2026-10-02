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
- `meta-check.yml` — *Unity Package Checks*: `.meta` file coverage, `package.json`, the CS0103 identifier heuristic, and the licence-free C# compile of `Editor/` + `Tests/` (`Tools~/compile_check.py`, .NET SDK against the NuGet Unity reference assemblies, both FBX define variants).
- `sonar-static-analysis.yml` — *SonarQube*: the compile-check build analysed with `dotnet-sonarscanner` on the self-hosted server (Community edition): `main` into the project itself with the quality gate enforced, a PR into a per-run throwaway project whose issues `Tools~/sonar-pr-check.mjs` filters to the PR's changed lines (any new finding reds the check, except the advisory rules S3776/S3267/S3358/S1075, reported only; S125/S1104/S107/S1168 are off in the begin step with their reasons). `sonar-backlog-report.yml` is the manual read-only triage report; `Tools~/sonar-mcp.sh` the read-only MCP server. Needs the `SONAR_TOKEN` secret; skips with a notice without it. Runbook: `the Sonar runbook in SashaRX/Space (docs/sonar-autofix.md; here the tools live in Tools~/ and the config in ~/.config/meshlab/)`.
- `version-bump.yml` — automates `package.json` version bumps.
- `test.yml` — EditMode test run on Unity 6000.0. License-gated: skips cleanly when no `UNITY_LICENSE`/`UNITY_SERIAL` secrets are configured. **This repository is on Unity Personal (free) tier**, and Unity disabled manual `.alf`→`.ulf` activation for Personal seats in 2024, so the test job is currently always **skipped** on GitHub-hosted runners. Local Test Runner remains the canonical pre-commit verification path. See `unity-ci-validation/SKILL.md` §License activation for the recipe and the path forward (self-hosted runner or Plus/Pro upgrade).
- `release.yml` — tag-triggered GitHub Release; verifies `v<version>` tag matches `package.json` and extracts the matching section from `CHANGELOG.md`.

## Deviations from `_shared/naming-conventions.md`

None at the canonical-target level. Historical namespace and package-ID deviations were resolved in the 1.0.0 release.

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
- Log via `UvtLog.Info` / `UvtLog.Warn` / `UvtLog.Error` — prefixed `[MeshLab]`.
- Use `Undo.RecordObject` / `Undo.AddComponent` / `Undo.DestroyObjectImmediate` for scene modifications.
- Call `RestoreWorkingMeshes()` before clearing/switching LODGroup context.
- Destroy temporary meshes (repacked, transferred, welded) when no longer needed.

## Migration history

- **1.0.0 (2026-04-20)** — package ID, namespace, and repository URL were standardized to the current UnityMeshLab identity. Downstream migration steps are recorded in `CHANGELOG.md`.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `migration-and-refactor-planner/SKILL.md`
- `unity-package-architect/SKILL.md`
- `unity-ci-validation/SKILL.md`
