# Checklist: Prefab safety

Run through every item before merging code that edits a prefab asset, a prefab instance override, or opens a PrefabStage. Each bullet includes a verification command.

## Which context are you in?

| Context | API path |
|---|---|
| Prefab asset (on disk, no instance) | `PrefabUtility.EditPrefabContentsScope` (2020.1+) |
| Prefab instance in a scene (override edit) | `Undo.RecordObject` + `PrefabUtility.RecordPrefabInstancePropertyModifications` |
| Prefab open in Prefab Mode (PrefabStage) | Scene-instance rules via `PrefabStageUtility.GetCurrentPrefabStage()` |
| Creating a prefab from an instance | `PrefabUtility.SaveAsPrefabAsset` with a temp-instance pattern |

## Prefab asset edit (preferred: disposable scope)

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabAssetPath))
{
    var root = scope.prefabContentsRoot;
    Mutate(root);
    // scope.Dispose saves and unloads automatically
}
```

- [ ] `EditPrefabContentsScope` is used for every asset-on-disk edit on Unity 2020.1+. Verify: `grep -n "EditPrefabContentsScope" path/to/file.cs`.
- [ ] If the disposable form is unavailable (pre-2020.1 fallback), the explicit form is used with both `LoadPrefabContents` and `UnloadPrefabContents(root, true)` in `try/finally`. Verify: `grep -n "LoadPrefabContents\|UnloadPrefabContents" path/to/file.cs`.
- [ ] No code path edits a prefab asset via `AssetDatabase.LoadAssetAtPath<GameObject>()` followed by direct mutation. Verify: `grep -n "LoadAssetAtPath<GameObject>" path/to/file.cs` and confirm the result is only read, never mutated.

## Prefab instance override edit (scene)

```csharp
Undo.RecordObject(component, "Edit override");
component.fieldValue = newValue;
PrefabUtility.RecordPrefabInstancePropertyModifications(component);
```

- [ ] BOTH calls are present, in THIS order. Verify: `grep -n -B1 -A3 "RecordPrefabInstancePropertyModifications" path/to/file.cs`.
- [ ] `serializedObject.ApplyModifiedProperties()` alone is insufficient for instance overrides that bypass the inspector — the explicit `RecordPrefabInstancePropertyModifications` call is still required.

## Temp-instance pattern (save as prefab)

```csharp
var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
try
{
    Mutate(instance);
    PrefabUtility.SaveAsPrefabAsset(instance, newPrefabPath);
}
finally
{
    Object.DestroyImmediate(instance);
}
```

- [ ] The temp instance is destroyed in `finally`, not at the end of `try`. Verify: read the method.
- [ ] The source prefab path is absolute to the project (`Assets/…` or `Packages/…`), forward-slashes only. Verify: `grep -n '\\\\' path/to/file.cs`.

## PrefabStage

- [ ] The namespace import is gated for 2021.2 (moved from `UnityEditor.Experimental.SceneManagement`). Verify: `grep -n "PrefabStage" path/to/file.cs` and read surrounding `#if UNITY_2021_2_OR_NEWER`.
- [ ] `PrefabStageUtility.GetCurrentPrefabStage()` nullability is checked before use.

## Further reading

- `unity-undo-prefab-safety/SKILL.md`
- `_shared/anti-patterns.md` (items 1–5)
- `_shared/version-gates.md` (PrefabStage + EditPrefabContentsScope gates)
- `_checklists/undo-safety.md`
