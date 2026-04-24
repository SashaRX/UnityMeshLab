# Naming conventions (canonical)

Canonical naming rules for every Unity UPM package under the `SashaRX` umbrella. This file is a Level-3 reference for the skills listed at the bottom. Per-repo deviations are recorded in each repo's `repo-conventions/SKILL.md` and take precedence over this file where explicitly documented.

## Package identifier (reverse-DNS, lowercase)

The `name` field in `package.json` is a reverse-DNS identifier, all lowercase, using hyphens to separate words in the leaf segment. The leaf segment must match the repository folder name when both are lowercased.

- Canonical: `com.sasharx.<package>` — e.g., `com.sasharx.prefabdoctor`, `com.sasharx.unitymeshlab`
- Prohibited: mixed case (`com.SashaRX.PrefabDoctor`), underscores (`com.sasharx.prefab_doctor`), missing vendor segment (`sasharx.prefabdoctor`)

Rationale: Unity's Package Manager is case-sensitive on disk on Linux and inside tarballs. `raw.githubusercontent.com` URLs that UPM resolves are also case-sensitive.

## Namespace (two segments, `SashaRX.<Package>`)

Every namespace block in `.cs` files under `Editor/`, `Runtime/`, and `Tests/` must begin with `SashaRX.<PackageName>`. The `<PackageName>` segment is PascalCase and matches the repository folder name.

- Canonical: `namespace SashaRX.PrefabDoctor`, `namespace SashaRX.UnityMeshLab.Editor`, `namespace SashaRX.UnityMeshLab.Tests`
- Prohibited: single-segment bare namespaces (`namespace LightmapUvTool`), three-or-more-segment vendor prefixes (`Com.SashaRX.PrefabDoctor`), arbitrary English words as root (`MyTools.PrefabDoctor`)

Rationale: single-segment namespaces collide with C# type names in IntelliSense, conflict with `using` aliases, and violate the reverse-DNS-analog convention used by every public Unity UPM package (UniTask, R3, NaughtyAttributes, MessagePipe, VContainer). A repository currently using a bare namespace must migrate via `migration-and-refactor-planner` before merging new code.

Sub-namespaces extend the two-segment root: `SashaRX.<Package>.Editor`, `SashaRX.<Package>.Runtime.Data`, `SashaRX.<Package>.Tests.Editor`.

## Asmdef name and `rootNamespace`

Every assembly definition file follows the same two-segment rule, with optional suffixes that describe platform or role.

| Role | Asmdef name | `rootNamespace` | `includePlatforms` |
|---|---|---|---|
| Runtime | `SashaRX.<Package>` | `SashaRX.<Package>` | `[]` (all platforms) |
| Editor | `SashaRX.<Package>.Editor` | `SashaRX.<Package>.Editor` | `["Editor"]` |
| Tests (Editor) | `SashaRX.<Package>.Tests.Editor` | `SashaRX.<Package>.Tests` | `["Editor"]` |
| Tests (Runtime) | `SashaRX.<Package>.Tests.Runtime` | `SashaRX.<Package>.Tests` | `[]` |

The asmdef `name` field and the asmdef file name (without `.asmdef`) must be identical. The `rootNamespace` field must match the `namespace` block of every `.cs` file under that asmdef.

## Folder layout

```
<Package>/
├── package.json
├── README.md
├── CHANGELOG.md
├── LICENSE
├── Editor/
│   ├── SashaRX.<Package>.Editor.asmdef
│   └── <PascalCase>/*.cs
├── Runtime/
│   ├── SashaRX.<Package>.asmdef
│   └── <PascalCase>/*.cs
├── Tests/
│   ├── Editor/
│   │   └── SashaRX.<Package>.Tests.Editor.asmdef
│   └── Runtime/
│       └── SashaRX.<Package>.Tests.Runtime.asmdef
├── Samples~/         (tilde-hidden; UPM-imported into consumer Assets/)
├── Documentation~/   (tilde-hidden; ignored by AssetDatabase)
└── .github/workflows/
```

Rules:

- PascalCase for every folder that contains `.cs` files.
- Tilde suffix (`~`) on `Samples~`, `Documentation~`, and `Tests~` when tests are shipped but excluded from consumer compilation. No `.meta` file is generated for tilde-hidden folders.
- Never place editor-only code under `Runtime/`. The asmdef platform filter is the enforcement boundary.

## File naming

- One public type per `.cs` file; file name equals the public type name (`PrefabHealthWindow.cs`).
- Partial classes: `PrefabHealthWindow.Toolbar.cs`, `PrefabHealthWindow.Data.cs`.
- Editor windows end in `Window`; property drawers end in `Drawer`; custom editors end in `Editor`; tests end in `Tests`.
- Interfaces begin with `I` (`IUvTool`, `IScannable`).

## MenuItem and asset paths

- MenuItem roots follow `Tools/<DisplayName>/<Action>` — e.g., `Tools/Prefab Doctor/Open Health Window`.
- `[CreateAssetMenu]` paths follow `Create/<DisplayName>/<AssetType>`.
- Asset paths in strings always use forward slashes, never backslashes, regardless of host OS.

## Deviation protocol

Any repository-specific deviation from the rules above must be documented in `repo-conventions/SKILL.md` with an explicit rationale. Deviations without a documented rationale are treated as defects. When a repo's `repo-conventions/SKILL.md` and this file conflict, the per-repo file wins.

## Further reading

- Every skill in this directory links to this file from its **Further reading** section.
- Related Level-3 references: `_shared/version-gates.md`, `_shared/anti-patterns.md`.
