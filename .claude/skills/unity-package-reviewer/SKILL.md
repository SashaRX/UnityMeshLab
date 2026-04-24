---
name: unity-package-reviewer
description: Audit a Unity UPM package for release readiness — verify package.json, asmdef platform filters, CHANGELOG conformance, SemVer bump correctness, Samples~ wiring, and absence of Assets/-only references. Use when preparing a release, merging a version bump PR, reviewing a UPM package for publication, or inspecting package.json changes. Runs through _checklists/package-release.md end to end.
paths: ["**/package.json", "**/CHANGELOG.md"]
---

# unity-package-reviewer

Review skill for Unity UPM packages at release time. The complementary author skill is `unity-package-architect`. This skill produces a findings report; it does not author content. For every finding, it cites the file, the line number, and the skill whose rule was violated.

## Scope and delegations

Covered here:

- SemVer correctness relative to diff since the previous tag.
- `package.json` fields and consistency with repository metadata.
- Asmdef platform filter audit.
- `samples` array vs `Samples~/` folder cross-check.
- CHANGELOG format and coverage.
- Missing docs or license.

Delegated elsewhere:

- **Code-level mutation safety** → `unity-undo-prefab-safety` finding cited by this reviewer.
- **Batching safety** → `unity-assetdatabase-tools` finding cited by this reviewer.
- **Migration execution** → `migration-and-refactor-planner`.
- **End-to-end release checklist** → `_checklists/package-release.md`.

## Review phases

Work through the phases in order. Each phase corresponds to a section of `_checklists/package-release.md`.

1. **SemVer decision**
2. **CHANGELOG coverage**
3. **`package.json` audit**
4. **Asmdef audit**
5. **Documentation and license**
6. **Build and tests**
7. **Tag and publish readiness**

Each phase ends with a findings subsection in the final report, severity-tagged.

## SemVer decision tree

| Change | Bump |
|---|---|
| Internal refactor, docstring, typo | PATCH |
| Bug fix with no API change | PATCH |
| New public type, new asmdef, new sample | MINOR |
| New optional `dependencies` entry | MINOR |
| New `versionDefines` symbol consumed elsewhere | MINOR |
| Public API removed or renamed | MAJOR |
| `unity` minimum bumped | MAJOR |
| Namespace changed | MAJOR |
| Asmdef name changed | MAJOR |

Algorithm:

1. `git log --oneline <prev-tag>..HEAD` to list commits.
2. `git diff <prev-tag>..HEAD --stat` to identify changed files.
3. For each `.cs` file changed, classify: internal / additive / removal-or-rename.
4. For each `.asmdef` / `package.json` / `Samples~/` change, classify similarly.
5. Highest classification wins; map to PATCH / MINOR / MAJOR.

Report: whether the proposed `version` in `package.json` matches the classification.

## CHANGELOG coverage

Required format: Keep-a-Changelog (`Added` / `Changed` / `Deprecated` / `Removed` / `Fixed` / `Security` sections; one `## [version] — YYYY-MM-DD` header per release).

Checks:

- The new version's header appears at the top of `CHANGELOG.md`.
- The header date is today or a recent date.
- Every commit since the previous tag maps to at least one CHANGELOG line, OR is explicitly internal (test-only, docs-only, tooling).
- There is no stale `## [Unreleased]` block that contradicts the new header.

## `package.json` audit

- `name` is `com.sasharx.<package>` and lowercased.
- `version` matches the CHANGELOG header and will match the git tag.
- `unity` equals or moves up from the previous tagged value; downgrades are a defect unless explicitly justified in CHANGELOG.
- `displayName` and `description` non-empty and under 200 chars.
- `repository.url` equals `git remote get-url origin` value.
- `dependencies` entries resolve; no stale entries.
- No non-standard fields (`type`, `main`, `module`).
- `samples[]` corresponds one-to-one to `Samples~/` folders.

## Asmdef audit

- Runtime asmdefs have empty `includePlatforms` and empty `excludePlatforms`.
- Editor asmdefs have `includePlatforms: ["Editor"]`.
- Test asmdefs include `defineConstraints: ["UNITY_INCLUDE_TESTS"]` and reference `UnityEngine.TestRunner` + `UnityEditor.TestRunner`.
- No Runtime asmdef references an Editor asmdef.
- `rootNamespace` matches the namespace used in every `.cs` file under the asmdef.
- Asmdef file basename equals `name` field.

## Documentation and license

- `README.md` install-via-git-URL example references the default branch or a tagged version.
- `LICENSE` SPDX identifier matches `license` in `package.json`.
- `Documentation~/` renders if present.

## Build and tests

- EditMode tests are present under `Tests/Editor/` or `Tests~/Editor/`.
- CI status on the target branch is green on every matrix row.
- Every `#if UNITY_*` directive has a compiling `#else` branch.

## Tag and publish readiness

- The proposed git tag name is `v<version>` and does not already exist.
- The tag target is HEAD of the default branch after the version bump commit.

## Report format

Emit Markdown with this structure. Every finding includes a severity, a file path with line number when applicable, and the skill whose rule was violated.

```
# Review: SashaRX/<Repo> at <short-sha>

## SemVer
- OK / FAIL: …

## CHANGELOG
- [severity] file:line — …  (skill: unity-package-reviewer)

## package.json
- [severity] package.json:NN — …

## Asmdef
- [severity] Editor/<Name>.asmdef:NN — …  (skill: unity-package-architect)

## Documentation and license
- …

## Build and tests
- …

## Tag and publish
- …

## Summary
- critical: N, warning: N, info: N
- verdict: READY / NOT-READY
```

Severity levels: `critical` (blocks release), `warning` (ship after fix), `info` (defer).

## Good vs bad finding pairs

**Bad:**

```
- CHANGELOG doesn't match version
```

**Good:**

```
- [critical] CHANGELOG.md:3 — header reads "## [1.2.3]" but package.json version is "1.3.0"
  (skill: unity-package-reviewer)
```

## Further reading

- `_checklists/package-release.md`
- `unity-package-architect/SKILL.md`
- `_shared/naming-conventions.md`
- `_shared/anti-patterns.md` (items 16–20)
