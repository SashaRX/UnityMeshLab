# Version gates (canonical)

Unity API availability thresholds used by every skill in this directory. Use this file as the single source of truth when gating code with `#if` directives or asmdef `versionDefines`. Target the minimum Unity version declared in `package.json` (`unity` field); anything newer requires an explicit gate.

## `UNITY_X_Y_OR_NEWER` preprocessor directives

| Directive | First Unity version | Enables |
|---|---|---|
| `UNITY_2020_1_OR_NEWER` | 2020.1 | `PrefabUtility.EditPrefabContentsScope` (IDisposable prefab asset edit) |
| `UNITY_2021_2_OR_NEWER` | 2021.2 | `PrefabStage` moved to `UnityEditor.SceneManagement`; `OnPostprocessAllAssets(…, bool didDomainReload)` overload |
| `UNITY_2021_3_OR_NEWER` | 2021.3 LTS | Baseline floor for most SashaRX packages |
| `UNITY_2022_2_OR_NEWER` | 2022.2 | UI Toolkit-first inspectors (`CreateInspectorGUI`) reach maturity |
| `UNITY_2023_1_OR_NEWER` | 2023.1 | Awaitable; improved job system APIs |
| `UNITY_6000_0_OR_NEWER` | Unity 6 | `AssetDatabase.AssetEditingScope` (IDisposable); new GPU Resident Drawer APIs |

There is no patch-level `_OR_NEWER` directive. Use asmdef `versionDefines` with `"name": "Unity"` if patch-level granularity is required.

## Asmdef `versionDefines` (interval notation)

`versionDefines` resolves one condition per entry against a named target, which can be a package name, module name, or `"Unity"`.

```json
"versionDefines": [
  {
    "name": "com.unity.formats.fbx",
    "expression": "[5.0.0,6.0.0)",
    "define": "LIGHTMAP_UV_TOOL_FBX_EXPORTER"
  },
  {
    "name": "Unity",
    "expression": "2022.2",
    "define": "UVTOOL_UI_TOOLKIT"
  }
]
```

Interval syntax (NuGet-style):

- `[1.7,2.4.1]` — inclusive on both ends.
- `[1.7,2.4.1)` — inclusive min, exclusive max.
- `2022.2` — bare value means `>= 2022.2`.

`defineConstraints` rejects compilation unless every listed symbol is defined. `versionDefines` is for *conditional* symbols; `defineConstraints` is for *required* symbols. Use `defineConstraints` on test asmdefs that require `UNITY_INCLUDE_TESTS`.

## Gate recipes by API

**`PrefabUtility.EditPrefabContentsScope`** — available since 2020.1. Use the disposable form unconditionally for any package targeting 2020.1 or newer.

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabAssetPath))
{
    // scope.prefabContentsRoot is a temporary GameObject hierarchy
}
```

**`PrefabStage` namespace** — moved from `UnityEditor.Experimental.SceneManagement` to `UnityEditor.SceneManagement` in 2021.2.

```csharp
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif
```

**`AssetDatabase.AssetEditingScope`** — Unity 6 adds an IDisposable batch scope. On earlier versions, fall back to manual `StartAssetEditing`/`StopAssetEditing` in a `try/finally`.

```csharp
#if UNITY_6000_0_OR_NEWER
using (new AssetDatabase.AssetEditingScope())
{
    BulkImport();
}
#else
AssetDatabase.StartAssetEditing();
try { BulkImport(); }
finally { AssetDatabase.StopAssetEditing(); }
#endif
```

**`OnPostprocessAllAssets` `didDomainReload` overload** — 2021.2+. When both overloads are defined, the richer one wins silently.

```csharp
#if UNITY_2021_2_OR_NEWER
static void OnPostprocessAllAssets(
    string[] imported, string[] deleted, string[] moved, string[] movedFrom,
    bool didDomainReload) { /* ... */ }
#else
static void OnPostprocessAllAssets(
    string[] imported, string[] deleted, string[] moved, string[] movedFrom) { /* ... */ }
#endif
```

**UI Toolkit `CreateInspectorGUI`** — usable on 2022.2+ for editor inspectors.

```csharp
#if UNITY_2022_2_OR_NEWER
public override VisualElement CreateInspectorGUI() { /* ... */ }
#else
public override void OnInspectorGUI() { /* ... */ }
#endif
```

## Package-minimum policy

- Declare the minimum supported Unity version in `package.json` (`"unity": "2021.3"` or similar). `unityRelease` optionally narrows to a specific release (`"0f1"`).
- Do not call any API newer than the declared minimum without an `#if` gate or a `versionDefines` entry.
- CI matrix must include the declared minimum plus every LTS in between and the current LTS.
- When bumping the minimum, use `migration-and-refactor-planner` and add a CHANGELOG entry under a major SemVer bump.

## Fallback policy

Every `#if` on a version directive must have an `#else` branch that compiles on the older version. Silent empty bodies are prohibited — log a warning or use the best available fallback API. Never leave an empty `#else { }`; this suppresses errors at runtime and masks missing functionality.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/anti-patterns.md`
