---
name: unity-undo-prefab-safety
description: Make every editor mutation undoable and every prefab edit safe. Use when code modifies scene GameObjects, components, prefab assets, or prefab instance overrides, or when using Undo, PrefabUtility, PrefabStage, or EditorUtility.SetDirty. ALWAYS call Undo.RecordObject before mutation, PrefabUtility.RecordPrefabInstancePropertyModifications after instance edits, and EditPrefabContentsScope (2020.1+) for asset edits. Do not use File.* for assets — use AssetDatabase APIs (delegate to unity-assetdatabase-tools).
paths: ["**/Editor/**/*.cs"]
---

# unity-undo-prefab-safety

Canonical rules for making every editor mutation undoable and every prefab edit safe. This is the flagship editor-mutation-safety skill. Asset-pipeline concerns (move, rename, batch import) live in `unity-assetdatabase-tools`.

## Three mutation contexts

Every editor mutation falls into exactly one context. Pick the right API for the context.

| Context | API path | Records undo? |
|---|---|---|
| Scene instance (GameObject/Component in an open scene) | `Undo.RecordObject` + mutation; or `SerializedObject` + `ApplyModifiedProperties` | Yes |
| Prefab instance override (scene instance of a prefab, editing an override) | `Undo.RecordObject` → mutate → `PrefabUtility.RecordPrefabInstancePropertyModifications` | Yes |
| Prefab asset (on disk, no scene instance) | `PrefabUtility.EditPrefabContentsScope` (2020.1+) | Editor re-opens dirty scene; no undo — users revert via git |

## `Undo.RecordObject`: what it covers and what it doesn't

`Undo.RecordObject(obj, name)` captures a serialized-property-delta snapshot of `obj`. It does NOT capture:

- Reparenting (`transform.parent = …`) — use `Undo.SetTransformParent`.
- Component addition (`gameObject.AddComponent<T>()`) — use `Undo.AddComponent<T>`.
- Object destruction (`Object.DestroyImmediate`) — use `Undo.DestroyObjectImmediate`.
- Object creation that must participate in the current undo group — use `Undo.RegisterCreatedObjectUndo` immediately after creation.

For full hierarchy edits (reorder, reparent, add/remove multiple children), use `Undo.RegisterFullObjectHierarchyUndo(root, name)`.

## Ordering rules

- `Undo.RecordObject` MUST be called BEFORE the mutation. After-the-fact calls capture the already-mutated state.
- `Undo.RegisterCreatedObjectUndo` MUST be called AFTER creation but BEFORE any subsequent `RecordObject`. Inserting it later orphans intermediate edits.
- For prefab instance overrides: `Undo.RecordObject` first, then mutate, then `PrefabUtility.RecordPrefabInstancePropertyModifications`. All three steps, in that order, for every instance edit.

## Grouping multiple mutations

When a single user action causes multiple mutations, collapse them into one undo group so that one Ctrl+Z reverses the whole batch.

```csharp
Undo.IncrementCurrentGroup();
var group = Undo.GetCurrentGroup();
Undo.SetCurrentGroupName("Refactor selection");
try
{
    foreach (var obj in Selection.gameObjects)
    {
        Undo.RecordObject(obj.transform, "Refactor");
        obj.transform.localScale *= 2f;
    }
}
finally
{
    Undo.CollapseUndoOperations(group);
}
```

The plan's `_checklists/undo-safety.md` repeats the idiom with a verification command.

## Prefab asset edit (preferred: disposable scope)

Available since Unity 2020.1. Opens the prefab in a temporary hierarchy, runs the body, saves on `Dispose`.

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabAssetPath))
{
    var root = scope.prefabContentsRoot;
    Mutate(root);
}
```

This is the correct path for every asset-on-disk edit. It avoids the `LoadPrefabContents`/`UnloadPrefabContents` ceremony and eliminates the common mistake of forgetting `UnloadPrefabContents(root, saveChanges: true)`.

Manual pattern (pre-2020.1 fallback):

```csharp
var root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
try
{
    Mutate(root);
    PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
}
finally
{
    PrefabUtility.UnloadPrefabContents(root);
}
```

## Prefab instance override edit

```csharp
Undo.RecordObject(component, "Edit override");
component.fieldValue = newValue;
PrefabUtility.RecordPrefabInstancePropertyModifications(component);
```

Both calls, in this order, for every instance edit. The `RecordObject` call alone marks the scene dirty; without `RecordPrefabInstancePropertyModifications`, the override is lost when the scene is reloaded or the prefab is re-applied.

For reverting overrides, use `PrefabUtility.RevertObjectOverride(obj, InteractionMode.UserAction)` so the action appears in the undo history.

## PrefabStage

`PrefabStage` is the API for prefabs currently open in Prefab Mode.

```csharp
#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif

var stage = PrefabStageUtility.GetCurrentPrefabStage();
if (stage != null)
{
    // stage.prefabContentsRoot is the live temporary hierarchy;
    // edits here follow scene-instance rules, not asset rules.
}
```

Always null-check `GetCurrentPrefabStage()`; Prefab Mode may not be active.

## Mesh safety

- `MeshFilter.mesh` in edit mode clones the shared asset and leaks the clone. Always use `sharedMesh`.
- Editing `sharedMesh` mutates the authored asset for every instance; `Undo.RecordObject` alone does NOT make this safe for subsequent project state. Clone first: `var clone = Object.Instantiate(filter.sharedMesh);`.
- `Mesh.UploadMeshData(true)` frees CPU-side mesh data after upload; the mesh becomes non-readable. Only call this when the mesh will not be inspected or re-edited.

## `EditorUtility.SetDirty`

`EditorUtility.SetDirty(asset)` marks an asset dirty for serialization but does NOT record an undo entry. It is a companion to `Undo.RecordObject`, not a substitute. Call order: `Undo.RecordObject(asset, name)` → mutate → `EditorUtility.SetDirty(asset)` if the asset is a `ScriptableObject` or similar.

## `EditorSceneManager.MarkSceneDirty`

`Undo.RecordObject` marks the scene dirty automatically. Call `MarkSceneDirty` explicitly only when mutating scene-level state that `RecordObject` does not cover (loaded scene metadata, lightmap data).

## Good vs bad pattern pairs

**Bad: `AddComponent` without undo**

```csharp
var c = go.AddComponent<MyBehaviour>();
c.value = 42;
```

**Good:**

```csharp
var c = Undo.AddComponent<MyBehaviour>(go);
Undo.RecordObject(c, "Configure");
c.value = 42;
```

**Bad: reparenting via `transform.parent`**

```csharp
child.transform.parent = newParent.transform;
```

**Good:**

```csharp
Undo.SetTransformParent(child.transform, newParent.transform, "Reparent");
```

**Bad: prefab asset edit through `LoadAssetAtPath`**

```csharp
var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
go.GetComponent<Foo>().value = 1; // mutates asset directly; no serialization guarantee
AssetDatabase.SaveAssets();
```

**Good: scope**

```csharp
using (var scope = new PrefabUtility.EditPrefabContentsScope(path))
{
    scope.prefabContentsRoot.GetComponent<Foo>().value = 1;
}
```

## Further reading

- `_checklists/undo-safety.md`
- `_checklists/prefab-safety.md`
- `_shared/anti-patterns.md`
- `_shared/version-gates.md`
