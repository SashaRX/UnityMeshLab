---
name: unity-package-bootstrap
description: Bootstrap a new Unity UPM package from the bundled _template/package-template/ — creates folder structure, substitutes {{PackageName}}, {{Namespace}}, {{DisplayName}}, {{UnityMinVersion}} placeholders, renames .template files, initializes git, and verifies the package imports cleanly. Use when the user asks to "create a new package", "scaffold a UPM package", "bootstrap a new Unity tool", or any request to start a new Unity editor extension.
---

# unity-package-bootstrap

Scaffolding skill for new Unity UPM packages. Reads from `_template/package-template/`, substitutes placeholders, renames `.template` files, and initializes git. The shape it produces matches `unity-package-architect` exactly.

## Scope and delegations

Covered here:

- Copying `_template/package-template/` to a target directory.
- Substituting `{{PackageName}}`, `{{Namespace}}`, `{{PackageId}}`, `{{DisplayName}}`, `{{Description}}`, `{{UnityMinVersion}}`, `{{Author}}`, `{{License}}`.
- Renaming `*.template` files to their final names (strips the `.template` suffix).
- Running `git init`, staging, creating an initial commit.
- Post-bootstrap verification.

Delegated elsewhere:

- **Package shape rules** → `unity-package-architect`.
- **CI workflow** → `unity-ci-validation`.
- **Release checks** → `unity-package-reviewer`.

## Template inventory

The template lives under `.claude/skills/_template/package-template/` relative to the repo that holds this skill. Contents:

```
_template/package-template/
├── package.json.template
├── README.md.template
├── CHANGELOG.md.template
├── LICENSE.template
├── .gitignore.template
├── Editor/
│   ├── SashaRX.{{PackageName}}.Editor.asmdef.template
│   └── PackageEditorEntryPoint.cs.template
├── Runtime/
│   ├── SashaRX.{{PackageName}}.asmdef.template
│   └── PackageRuntimeEntryPoint.cs.template
├── Tests/Editor/
│   ├── SashaRX.{{PackageName}}.Tests.Editor.asmdef.template
│   └── SmokeTests.cs.template
├── Samples~/
│   └── Basic/
│       └── README.md.template
└── Documentation~/
    └── index.md.template
```

Every `.template` file contains `{{...}}` placeholders. The bootstrap pipeline walks the tree, substitutes, renames, and the user sees a clean package tree with no template artifacts.

## Parameters

Collect from the invoking user, with defaults:

| Parameter | Default | Example |
|---|---|---|
| `PackageName` | required | `PrefabDoctor` (PascalCase) |
| `Namespace` | `SashaRX.{PackageName}` | `SashaRX.PrefabDoctor` |
| `PackageId` | `com.sasharx.{lowercase(PackageName)}` | `com.sasharx.prefabdoctor` |
| `DisplayName` | `{PackageName}` split on PascalCase boundaries | `Prefab Doctor` |
| `Description` | required | `Nested prefab override conflict finder.` |
| `UnityMinVersion` | `2021.3` | `2022.3` or `6000.0` |
| `Author` | `SashaRX` | |
| `License` | `MIT` | |
| `Year` | current year | `2026` (for LICENSE) |
| `Date` | today | `2026-04-20` (for CHANGELOG) |

Reject `PackageName` values that are not PascalCase, contain spaces, or equal a C# reserved word.

## Execution sequence

1. **Resolve target path.** Default: `<cwd>/<PackageName>`. Fail if the directory already exists and is non-empty.
2. **Copy template tree.** Every file and folder is copied; `.template` suffixes remain on files at this point.
3. **Substitute placeholders.** For each file, replace every `{{Placeholder}}` token with the resolved value. Whole-word substitution only.
4. **Rename `.template` files.** Strip the `.template` suffix from every file name. Also rename folders that contain `{{PackageName}}` in their path (the asmdef folder is not one of these; the asmdef *file* name uses the namespace).
5. **Verify.** Parse `package.json` as JSON; parse each `.asmdef` as JSON; confirm no `{{...}}` markers remain anywhere under the new package.
6. **Initialize git.** `git init`, `git add .`, commit with message `chore: bootstrap <PackageName> from template`.
7. **Report.** Print the file tree, the resolved parameters, and the next steps (open in Unity, add CI via `unity-ci-validation`, add samples).

## Post-bootstrap verification checklist

- [ ] `jq . package.json >/dev/null` succeeds.
- [ ] `jq . Editor/*.asmdef >/dev/null` and `jq . Runtime/*.asmdef >/dev/null` succeed.
- [ ] `grep -rn '{{' .` returns nothing.
- [ ] `git status` shows a clean tree after the initial commit.
- [ ] File tree matches the canonical layout in `unity-package-architect/SKILL.md`.
- [ ] Namespace in `Editor/PackageEditorEntryPoint.cs` matches the `Namespace` parameter.

## Integration with `unity-ci-validation`

After the initial commit, prompt the user whether to add a GameCI workflow:

- If yes, delegate to `unity-ci-validation` to author `.github/workflows/ci.yml` targeting `UnityMinVersion` and the current LTS.
- If no, add a `## CI` section to `README.md` pointing at `unity-ci-validation/SKILL.md` for later setup.

## Good vs bad execution

**Bad:** copy the template, open in Unity, manually edit `{{PackageName}}` in 12 files.

**Good:** run the substitution + rename + verify pipeline in one pass; commit once; open in Unity to a ready package.

## Further reading

- `unity-package-architect/SKILL.md`
- `unity-ci-validation/SKILL.md`
- `_shared/naming-conventions.md`
- `_checklists/package-release.md`
