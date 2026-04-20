---
name: migration-and-refactor-planner
description: Plan a safe migration inside a Unity UPM package — namespace rename, asmdef restructure, Unity minimum-version bump, or API deprecation. Use when renaming a namespace across many files, splitting or merging asmdefs, bumping the "unity" field in package.json, or deprecating public API that downstream samples or tests depend on. Always sequence snapshot then migrate then regenerate GUIDs only if unavoidable then run tests then bump SemVer.
---

# migration-and-refactor-planner

Planner skill for intra-package migrations. Scope is bounded to a single UPM package; cross-repo refactors are out of scope. Every migration follows the same phase template: snapshot, discover, plan, execute, validate, commit, bump.

## Scope and delegations

Covered here:

- Namespace rename across `.cs` files and asmdef `rootNamespace` / `name`.
- Asmdef split and merge with GUID preservation.
- Unity minimum-version bump with API audit.
- Public API deprecation via `[Obsolete]` progression.
- Adding or moving `Tests/`, `Samples~/`, `Documentation~/`.

Delegated elsewhere:

- **New package from scratch** → `unity-package-bootstrap`.
- **Pre-release audit** → `unity-package-reviewer`.
- **Per-repo conventions that drive the migration target** → `repo-conventions`.
- **Repository-wide audit** → `repo-auditor`.

## Phase template (use for every migration)

1. **Snapshot**: `git tag -a pre-<migration>-<date>` on clean HEAD. Creates the rollback target.
2. **Discover**: run a read-only scan to catalogue every affected file; write the list to `/tmp/<migration>-files.txt`.
3. **Plan**: write the migration steps to `/tmp/<migration>-plan.md` with one step per commit.
4. **Execute**: one commit per step; verify compile between steps.
5. **Validate**: run tests in the editor and CI; confirm no downstream consumers broke.
6. **Commit**: squash-or-merge only after validation.
7. **Bump**: update `CHANGELOG.md` and `package.json` `version` per SemVer, tag the release.

Rollback at any step: `git reset --hard pre-<migration>-<date>` and `git tag -d pre-<migration>-<date>`.

## Playbook: namespace rename

Target: move every namespace from `<Old>` to `SashaRX.<Package>`.

Discovery:

```bash
grep -rn "^namespace $OLD" --include='*.cs' | tee /tmp/ns-rename-files.txt
git ls-files '*.asmdef' | tee /tmp/ns-rename-asmdefs.txt
```

Execute:

- Replace namespace blocks in every listed `.cs` file; word-boundary-safe replacement only.
- Update every `using $OLD…;` to `using $NEW…;`.
- Update each asmdef's `rootNamespace` to the new root; update `name` if it encoded the old namespace.
- Keep the asmdef file name unchanged on disk until the last step to preserve GUIDs. Rename the asmdef file as a final, isolated commit.

Validate:

- Compile in the minimum supported Unity version.
- Run EditMode tests.
- Verify `git grep "^namespace $OLD"` returns nothing.
- Verify no unresolved `using` directives in the CS compiler output.

Commit plan (one commit each):

1. Replace namespace blocks in `Runtime/`.
2. Replace namespace blocks in `Editor/`.
3. Replace namespace blocks in `Tests/`.
4. Update `using` directives across all three.
5. Update asmdef `rootNamespace`.
6. Rename asmdef file (if applicable) via `AssetDatabase.RenameAsset` in editor, or direct rename + `.meta` preservation; commit the rename as a single-file change so GUID preservation is obvious in review.

## Playbook: asmdef split or merge

Rule: **never delete an asmdef and recreate with the same name**. The GUID is lost; references in other asmdefs silently drop.

Split:

1. Create the new asmdef file; copy `references` minus the lines that move out.
2. Move `.cs` files to the new asmdef's folder via `AssetDatabase.MoveAsset` (preserves GUIDs).
3. Update references in downstream asmdefs to point at the new name.
4. Commit each step.

Merge:

1. Pick one asmdef as the survivor; keep its file (GUID stable).
2. Move `.cs` from the other asmdef(s) into the survivor's folder.
3. Update references in downstream asmdefs: remove the merged asmdef, keep the survivor.
4. Delete the merged asmdef file last; its `.meta` goes with it.

## Playbook: Unity minimum-version bump

Target: raise `package.json` `unity` from `<old>` to `<new>`.

Discovery:

```bash
grep -rn "#if UNITY_" --include='*.cs' Editor Runtime | tee /tmp/unity-bump-gates.txt
git grep -n "UNITY_[0-9_]*_OR_NEWER" -- Editor Runtime
```

Audit:

- Identify APIs used in the codebase that were added after `<old>` but before `<new>` — those gates are now unconditional and the `#else` branches can be removed.
- Identify APIs deprecated between `<old>` and `<new>` — replace with current equivalents.
- Update CI matrix to include `<new>` and drop rows below `<new>`.

Execute:

1. Update `package.json`: `"unity": "<new>"`.
2. Remove unnecessary `#if UNITY_…_OR_NEWER` gates whose threshold is `<= <new>`.
3. Replace deprecated APIs.
4. Update CI matrix in `.github/workflows/*.yml`.
5. Add a CHANGELOG `## Changed` entry calling out the new minimum.

SemVer: this is a MAJOR bump.

## Playbook: public API deprecation

Phased `[Obsolete]` progression; never remove a public symbol in one step.

1. Release N: add `[Obsolete("Use NewApi instead.", error: false)]`. CHANGELOG `## Deprecated`.
2. Release N+1 (next MINOR): keep deprecation warning; add `NewApi` and document the migration path. CHANGELOG `## Added` for `NewApi`.
3. Release N+2 (next MINOR): change to `error: true`. CHANGELOG `## Deprecated` with removal target.
4. Release N+3 (next MAJOR): remove the symbol. CHANGELOG `## Removed`.

Rule: samples and tests that reference the deprecated symbol must be updated in step 2.

## Playbook: adding `Tests/`, `Samples~/`, `Documentation~/`

1. Create the folder.
2. Create the asmdef (for `Tests/Editor/`).
3. Add a single smoke test or a placeholder `README.md`.
4. Update `package.json` `samples` array if adding `Samples~/`.
5. Commit each step.

## Validation rules

After any migration, run:

- `git grep -n "^namespace" -- Editor Runtime Tests` and confirm no stale namespaces.
- `jq '.name, .rootNamespace, .references' **/*.asmdef` and confirm consistency.
- `jq '.unity' package.json` against the CI matrix declared in `.github/workflows/*.yml`.
- EditMode tests green on minimum Unity version and current LTS.

## Good vs bad plan pairs

**Bad:** single commit renaming namespace in 40 files across Runtime, Editor, and Tests plus renaming two asmdef files plus updating three downstream references.

**Good:** six commits matching the "Commit plan" list above, each reviewable and revertable independently.

## Further reading

- `_checklists/package-release.md`
- `_shared/version-gates.md`
- `_shared/naming-conventions.md`
- `unity-package-architect/SKILL.md`
- `unity-package-reviewer/SKILL.md`
