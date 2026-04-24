# Checklist: Undo safety

Run through every item before merging code that mutates a scene `GameObject`, a `Component`, or a serialized property of a Unity object. Each bullet includes a verification command.

## Decision tree (pick one path)

1. **Editing a scene instance field?** → use `Undo.RecordObject(obj, name)` before the mutation; mutate via `SerializedObject` when possible.
2. **Reparenting a transform?** → use `Undo.SetTransformParent(child, newParent, name)`; do NOT set `transform.parent` directly.
3. **Adding a component?** → use `Undo.AddComponent<T>(go)`; do NOT call `go.AddComponent<T>()` directly.
4. **Destroying an object?** → use `Undo.DestroyObjectImmediate(obj)` in editor contexts; raw `Object.DestroyImmediate` is for temporary previews only.
5. **Creating a new object that must undo as a single unit?** → `Undo.RegisterCreatedObjectUndo(obj, name)` immediately after creation, before any further mutation.
6. **Editing a prefab instance override?** → see `prefab-safety.md`.
7. **Editing a prefab asset?** → see `prefab-safety.md`.

## Checks

- [ ] `Undo.RecordObject` is called BEFORE the mutation, not after. Verify: `grep -n -B1 -A3 "RecordObject" path/to/file.cs` and read.
- [ ] Every related mutation is collapsed into a single undo group. Verify: grep for `IncrementCurrentGroup`, `SetCurrentGroupName`, `CollapseUndoOperations` around the batch.
- [ ] No bare `go.AddComponent<T>()` in editor code. Verify: `grep -rn --include='*.cs' "\.AddComponent<" Editor/`.
- [ ] No bare `DestroyImmediate` in editor code except for explicitly-temporary objects created and destroyed in the same `using`/`try-finally`. Verify: `grep -rn --include='*.cs' "DestroyImmediate" Editor/`.
- [ ] No direct assignment to `transform.parent`. Verify: `grep -rn --include='*.cs' "transform.parent =" Editor/`.
- [ ] `MeshFilter.mesh` is never read in editor code — only `sharedMesh`. Verify: `grep -rn --include='*.cs' "\.mesh[^F]" Editor/`.
- [ ] Every `sharedMesh` write is preceded by a clone (`Object.Instantiate(filter.sharedMesh)`) unless the intent is to mutate the asset. Verify manually.
- [ ] `CustomEditor` code mutates only through `SerializedObject.FindProperty` + `ApplyModifiedProperties`; no direct field writes on `target`. Verify: read the `OnInspectorGUI` body.

## Undo group idiom

```csharp
var group = Undo.GetCurrentGroup();
Undo.SetCurrentGroupName("Refactor selection");
try
{
    foreach (var obj in Selection.objects)
    {
        Undo.RecordObject(obj, "Refactor");
        Mutate(obj);
    }
}
finally
{
    Undo.CollapseUndoOperations(group);
}
```

- [ ] The idiom above (or an equivalent `using` helper) is used wherever more than one object is mutated in a single user action.

## Further reading

- `unity-undo-prefab-safety/SKILL.md`
- `_shared/anti-patterns.md` (items 1–5)
- `_checklists/prefab-safety.md` for prefab instance/asset paths
