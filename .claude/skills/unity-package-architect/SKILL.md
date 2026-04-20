---
name: unity-package-architect
description: ALWAYS invoke this skill when creating a new Unity UPM package, restructuring a package, editing package.json, adding or splitting asmdefs, or touching Runtime/Editor/Tests/Samples~/Documentation~ layout. Do not place Runtime code in an Editor asmdef or vice versa; do not use single-segment namespaces; do not ship Samples/ without the trailing tilde. Mandatory — namespaces follow SashaRX.<PackageName>, asmdef name equals file basename, includePlatforms and excludePlatforms are mutually exclusive.
paths: ["**/package.json", "**/*.asmdef", "**/*.asmref"]
---

# unity-package-architect

Authoritative skill for Unity UPM package layout. Every new or restructured package must conform to this shape. The companion `unity-package-reviewer` audits against these rules; `unity-package-bootstrap` scaffolds a fresh package from the template.

## Scope and delegations

Covered here:

- Folder tree: `Editor/`, `Runtime/`, `Tests/`, `Samples~/`, `Documentation~/`, `Native~/`.
- `package.json` field rules.
- Asmdef pair pattern (Runtime + Editor + Tests).
- `versionDefines` and `defineConstraints`.
- `Samples~` and `samples` array correspondence.
- `.gitignore` / `.npmignore` rules for UPM.

Delegated elsewhere:

- **Pre-release checks** → `unity-package-reviewer` and `_checklists/package-release.md`.
- **Migration (namespace rename, Unity bump)** → `migration-and-refactor-planner`.
- **New package from template** → `unity-package-bootstrap`.

## Canonical folder tree

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
│   └── Runtime/               (optional)
│       └── SashaRX.<Package>.Tests.Runtime.asmdef
├── Samples~/
│   └── <SampleName>/
├── Documentation~/
│   ├── index.md
│   └── images/
└── Native~/                   (optional, C++ sources excluded from AssetDatabase)
```

Rules:

- `Editor/` and `Runtime/` sit at the package root; never nest them inside each other.
- Tilde-hidden folders (`Samples~`, `Documentation~`, `Tests~`, `Native~`) have no `.meta` files and are ignored by AssetDatabase.
- `Tests/` without a tilde compiles into the consumer project if they import the package; `Tests~/` ships tests that stay hidden unless imported explicitly.

## `package.json` field-by-field

```json
{
  "name": "com.sasharx.<package>",
  "version": "1.2.3",
  "displayName": "<Display Name>",
  "description": "Single-paragraph description; shown in Package Manager.",
  "unity": "2021.3",
  "unityRelease": "0f1",
  "documentationUrl": "https://github.com/SashaRX/<Repo>",
  "changelogUrl": "https://github.com/SashaRX/<Repo>/blob/main/CHANGELOG.md",
  "licensesUrl": "https://github.com/SashaRX/<Repo>/blob/main/LICENSE",
  "repository": { "type": "git", "url": "https://github.com/SashaRX/<Repo>.git" },
  "author": { "name": "SashaRX" },
  "license": "MIT",
  "dependencies": {
    "com.unity.editorcoroutines": "1.0.0"
  },
  "samples": [
    { "displayName": "Basic", "description": "Minimum example", "path": "Samples~/Basic" }
  ]
}
```

Field rules:

- `name` — reverse-DNS, all lowercase; `com.sasharx.<package>`. Must match the package folder name (lowercased).
- `version` — SemVer; matches the git tag `v<version>`.
- `unity` — the minimum supported LTS (`"2021.3"` / `"2022.3"` / `"6000.0"`). No patch.
- `unityRelease` — optional release modifier, e.g., `"0f1"`. Omit unless you require a specific patch.
- `displayName`, `description` — shown in Package Manager; keep under 200 chars.
- `repository`, `documentationUrl`, `changelogUrl`, `licensesUrl` — populate when the repo is public.
- `dependencies` — other UPM packages with SemVer ranges. Do not list Unity modules.
- `samples` — one entry per folder under `Samples~/`; entries appear in Package Manager as Import buttons.

Non-standard fields (`type`, `main`, `module`) must not appear.

## Asmdef pair pattern

Every package ships at least two asmdefs — Runtime and Editor — and at least one test asmdef.

**Runtime asmdef** (`Runtime/SashaRX.<Package>.asmdef`):

```json
{
  "name": "SashaRX.<Package>",
  "rootNamespace": "SashaRX.<Package>",
  "references": [],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```

**Editor asmdef** (`Editor/SashaRX.<Package>.Editor.asmdef`):

```json
{
  "name": "SashaRX.<Package>.Editor",
  "rootNamespace": "SashaRX.<Package>.Editor",
  "references": [ "SashaRX.<Package>" ],
  "includePlatforms": [ "Editor" ],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "autoReferenced": false,
  "defineConstraints": [],
  "versionDefines": []
}
```

**Tests (Editor) asmdef** (`Tests/Editor/SashaRX.<Package>.Tests.Editor.asmdef`):

```json
{
  "name": "SashaRX.<Package>.Tests.Editor",
  "rootNamespace": "SashaRX.<Package>.Tests",
  "references": [
    "SashaRX.<Package>",
    "SashaRX.<Package>.Editor",
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner"
  ],
  "includePlatforms": [ "Editor" ],
  "defineConstraints": [ "UNITY_INCLUDE_TESTS" ],
  "optionalUnityReferences": [ "TestAssemblies" ]
}
```

Each asmdef file basename equals the `name` field. `rootNamespace` matches the namespace block of every `.cs` file beneath.

## `versionDefines` vs `defineConstraints`

| Field | Role |
|---|---|
| `versionDefines` | Conditionally defines a symbol when a dependency or Unity version matches the expression. |
| `defineConstraints` | Rejects compilation unless every listed symbol is defined. |

Example: gate code on the presence of the FBX exporter package.

```json
"versionDefines": [
  { "name": "com.unity.formats.fbx", "expression": "[5.0.0,6.0.0)", "define": "LIGHTMAP_UV_TOOL_FBX_EXPORTER" }
]
```

Expression syntax follows NuGet interval notation. See `_shared/version-gates.md` for the full recipes.

## `Samples~` and the `samples` array

Every `Samples~/<SampleName>/` folder corresponds to one entry in the `samples` array with `path: "Samples~/<SampleName>"`. Unity imports the folder into `Assets/Samples/<displayName>/<version>/<SampleName>` on demand.

Rules:

- Sample folder names are PascalCase without spaces.
- Each sample contains a small `README.md`.
- Samples that need a scene reference it with a forward-slash relative path only.

## `Documentation~` and `Tests~` conventions

- `Documentation~` is the canonical docs folder; consumers never see it compiled.
- `Tests~` is used for tests that ship but should NOT compile in consumer projects by default. `Tests/` without the tilde is used when tests should run as part of the consumer's Test Runner.

## `Native~`

Native source (C/C++) that produces platform binaries. Binaries are committed to `Plugins/` with the appropriate platform filter in their `.meta`. Keep sources in `Native~` so they do not participate in AssetDatabase.

## `.gitignore` and `.npmignore`

- Always commit `.meta` files. Never add a broad `*.meta` rule to `.gitignore`.
- Ignore `Library/`, `Logs/`, `Temp/`, `UserSettings/` at the repository root.
- For UPM publishing, `.npmignore` excludes `Documentation~/` and `Tests~/` from the tarball if you want smaller packages; however, canonical SashaRX packages ship everything so `.npmignore` stays empty.

## Good vs bad pattern pairs

**Bad: flat `Assets/`-style layout**

```
MyPackage/
├── package.json
└── Assets/
    ├── Scripts/
    └── Editor/
```

**Good: canonical tree above.**

**Bad: single asmdef containing both Editor and Runtime code.**

**Good: Runtime + Editor pair with the Editor asmdef referencing the Runtime asmdef.**

**Bad: `"unity": "2022"` (no minor).**

**Good: `"unity": "2022.3"`.**

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `_checklists/package-release.md`
- `unity-package-reviewer/SKILL.md`
- `unity-package-bootstrap/SKILL.md`
