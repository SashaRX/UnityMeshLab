---
name: repo-auditor
description: Audit a Unity UPM repository's health — skill directory structure, AGENTS.md/CLAUDE.md coherence, CI workflow presence, LICENSE, CHANGELOG, package.json correctness, .gitignore safety. Use when onboarding a new repo, before a release, or when asked to "audit", "review the repo", or "check project health". Produces a prioritized findings report with line-referenced citations.
---

# repo-auditor

Read-only audit skill for Unity UPM repositories. Produces a prioritized Markdown report; never edits files. Findings are actionable — each cites the file, the line number when applicable, and the skill that owns the rule being checked so the reader can route a fix to the right author skill.

## Scope and delegations

Covered here:

- Skills directory structure and coherence with `.claude/skills/skills-overhaul-plan.md` if present.
- Agent docs (`AGENTS.md`, `CLAUDE.md`) presence and consistency with each other.
- `.github/workflows/` presence for Unity CI and release automation.
- `LICENSE`, `CHANGELOG.md`, `README.md` presence and format.
- `package.json` correctness (delegates per-field checks to `unity-package-reviewer`).
- Asmdef platform filter sanity (delegates to `unity-package-reviewer`).
- `.gitignore` safety (no `*.meta` rule, no `Library/` commit).
- `Samples~` / `Documentation~` presence when declared.

Delegated elsewhere:

- **Per-release audit** → `unity-package-reviewer` and `_checklists/package-release.md`.
- **Migration planning from findings** → `migration-and-refactor-planner`.
- **Rule authority** → each finding cites the skill owning the rule.

## Audit dimensions

| Dimension | Checks |
|---|---|
| Skills | `.claude/skills/` exists, contains canonical skills, `_shared/` and `_checklists/` populated, no orphan references, every skill has valid frontmatter |
| Agent docs | `AGENTS.md` or `CLAUDE.md` present; if both, they do not contradict; neither duplicates skill content |
| CI workflows | `.github/workflows/*.yml` includes a test workflow targeting the minimum Unity version in `package.json` |
| License | `LICENSE` file at repo root; SPDX matches `package.json` `license` |
| Changelog | `CHANGELOG.md` follows Keep-a-Changelog; latest version matches `package.json` `version` |
| Readme | `README.md` present; install-via-git-URL example cites default branch or a tagged version |
| package.json | Reverse-DNS name, SemVer version, `unity` LTS, no non-standard fields |
| Asmdef | Runtime/Editor pair present, platform filters correct, `rootNamespace` consistent |
| Namespace | Every `.cs` file uses `SashaRX.<Package>` (or documented deviation in `repo-conventions`) |
| .gitignore | No `*.meta` rule; `Library/`, `Logs/`, `Temp/`, `UserSettings/` ignored |
| Samples | Every `samples[]` entry in `package.json` has a matching `Samples~/<Name>/` folder |
| Documentation | `Documentation~/` exists if referenced by `documentationUrl` field |

## Report format

```
# Audit: SashaRX/<Repo> at <short-sha>

## Summary
- critical: N, warning: N, info: N
- verdict: CLEAN / NEEDS-WORK / BROKEN

## Skills
- [severity] .claude/skills/<name>/SKILL.md:NN — <problem>  (skill: <owning-skill>)

## Agent docs
- …

## CI workflows
- …

## License, Changelog, Readme
- …

## package.json
- …  (skill: unity-package-reviewer)

## Asmdef
- …  (skill: unity-package-architect)

## Namespace
- …  (skill: unity-package-architect / repo-conventions)

## .gitignore
- …

## Samples and Documentation
- …

## Recommended follow-up
- Route `migration-and-refactor-planner` for: <list of migrations>
```

Severity levels:

- `critical` — blocks release or development (no CI, no LICENSE, namespace non-compliance).
- `warning` — ship after fix (missing sample README, outdated CHANGELOG header).
- `info` — defer (style drift, optional folders absent).

## Discovery commands

The skill runs these commands (read-only) to build the report:

```bash
test -f package.json && jq -r '.name, .version, .unity, .license' package.json
test -f LICENSE && head -3 LICENSE
test -f CHANGELOG.md && head -20 CHANGELOG.md
test -f README.md && head -40 README.md
test -d .claude/skills && find .claude/skills -maxdepth 2 -name SKILL.md | sort
test -d .github/workflows && ls .github/workflows
git grep -n "^namespace " -- 'Editor/*.cs' 'Runtime/*.cs' | head -20
git ls-files '*.asmdef'
git check-ignore -v Library/ Logs/ Temp/ UserSettings/ 2>/dev/null
grep -n '^\*\.meta$' .gitignore 2>/dev/null
```

## Integration with other skills

Every finding should name a follow-up target:

- Skills directory findings → this skill (`repo-auditor`) and `skills-overhaul-plan.md` if present.
- Agent doc findings → manual resolution; this skill does not author.
- CI findings → `unity-ci-validation`.
- package.json / asmdef findings → `unity-package-reviewer` for audit, `unity-package-architect` for fix.
- Namespace findings → `migration-and-refactor-planner`.
- Bootstrap findings (missing package skeleton) → `unity-package-bootstrap`.

## Good vs bad finding pairs

**Bad:**

```
- There's no license.
```

**Good:**

```
- [critical] LICENSE — missing; package.json:7 declares "license": "MIT" but no LICENSE file exists.
  Route: add MIT LICENSE via unity-package-bootstrap template.
```

## Further reading

- `_checklists/package-release.md`
- `_shared/anti-patterns.md`
- `unity-package-reviewer/SKILL.md`
- `unity-package-architect/SKILL.md`
- `migration-and-refactor-planner/SKILL.md`
