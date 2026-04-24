# Checklist: UPM package release

Run through every item before tagging a release. Each bullet includes a verification command.

## SemVer decision

Choose exactly one bump based on the highest-impact change in the release:

| Bump | Trigger |
|---|---|
| PATCH | Bug fixes, documentation, internal refactors. No public API change. |
| MINOR | New public API, new asmdef, new sample, additive versionDefines. No breaking change. |
| MAJOR | Any removed or renamed public type/member, Unity minimum bump, namespace change, asmdef name change. |

- [ ] The SemVer bump matches the highest-impact change. Verify: `git log --oneline <previous-tag>..HEAD` and read every entry.
- [ ] `package.json` `version` has been updated and committed. Verify: `jq -r .version package.json`.

## CHANGELOG

- [ ] `CHANGELOG.md` follows Keep-a-Changelog format with sections `Added` / `Changed` / `Deprecated` / `Removed` / `Fixed` / `Security`. Verify: `head -30 CHANGELOG.md`.
- [ ] Every commit since the previous tag has a CHANGELOG entry, or is explicitly excluded as internal. Verify: diff `git log <previous-tag>..HEAD --oneline` against `CHANGELOG.md`.
- [ ] The new version header matches `package.json` `version` exactly. Verify: `grep -n "^## \[" CHANGELOG.md | head -1`.

## package.json

- [ ] `name` is `com.sasharx.<package>` and matches the repository folder name. Verify: `jq -r .name package.json`.
- [ ] `unity` field matches the declared minimum; `unityRelease` is set if a specific patch is required. Verify: `jq -r '.unity, .unityRelease' package.json`.
- [ ] `repository.url` points to the canonical `https://github.com/SashaRX/<Repo>.git` URL. Verify: `jq -r .repository.url package.json` and compare to `git remote get-url origin`.
- [ ] `dependencies` entries resolve in the latest Unity Package Manager. Verify: open in Unity and watch `Window > Package Manager` for resolve errors.
- [ ] `samples` array entries correspond one-to-one to folders under `Samples~/`. Verify: `jq -r '.samples[].path' package.json | sed 's|^Samples~/||'` matches `ls Samples~`.
- [ ] No non-standard fields (`type`, `main`, `module`) are present. Verify: `jq 'keys' package.json`.

## Asmdef audit

- [ ] Runtime asmdef has empty `includePlatforms` and empty `excludePlatforms`. Verify: `jq '.includePlatforms, .excludePlatforms' Runtime/*.asmdef`.
- [ ] Editor asmdef has `includePlatforms: ["Editor"]`. Verify: `jq '.includePlatforms' Editor/*.asmdef`.
- [ ] Test asmdef has `defineConstraints: ["UNITY_INCLUDE_TESTS"]`. Verify: `jq '.defineConstraints' Tests/**/*.asmdef`.
- [ ] No Runtime asmdef references an Editor asmdef. Verify: `jq '.references' Runtime/*.asmdef` and confirm none end in `.Editor`.
- [ ] `rootNamespace` of every asmdef matches the namespace used in the files under it. Verify by reading.

## Documentation and license

- [ ] `README.md` install-via-git-URL section points at the current default branch or tag. Verify: `grep -n "git\+https" README.md`.
- [ ] `LICENSE` exists at the package root and its SPDX identifier matches `license` in `package.json`. Verify: `jq -r .license package.json` and `head -3 LICENSE`.
- [ ] `Documentation~/` renders (index.md or TableOfContents present). Verify: `ls Documentation~` if present.

## Build and tests

- [ ] Compile passes on the declared minimum Unity version. Verify: CI result on the matrix minimum job.
- [ ] Compile passes on the current LTS. Verify: CI result.
- [ ] Every `#if UNITY_*` gate has an `#else` branch that compiles. Verify: `grep -rn "^#if UNITY_" Editor Runtime` and read each site.
- [ ] EditMode tests pass. Verify: CI result, or `Window > General > Test Runner > Run All` in the editor.

## Tag and publish

- [ ] The git tag name matches `v<version>` exactly (e.g., `v1.2.3`). Verify: `git tag -l | tail`.
- [ ] The tag points at HEAD of the default branch after CHANGELOG + version bump commits. Verify: `git log --oneline <tag> | head -1`.
- [ ] Installation via `https://github.com/SashaRX/<Repo>.git` resolves the tagged version in a consumer project's `Packages/manifest.json`. Verify in a scratch Unity project.

## Further reading

- `unity-package-reviewer/SKILL.md`
- `unity-package-architect/SKILL.md`
- `_shared/naming-conventions.md`
- `_shared/anti-patterns.md` (items 16–20)
