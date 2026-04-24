---
name: unity-serialized-workflow
description: Implement CustomEditor and PropertyDrawer classes with correct SerializedObject lifecycle. Use when writing CustomEditor, CustomPropertyDrawer, EditorWindow with Inspector-style panels, or any code using SerializedObject/SerializedProperty/FindProperty/FindPropertyRelative. Always call serializedObject.Update() first and ApplyModifiedProperties() last; never call Update() mid-modification.
paths: ["**/Editor/**/*.cs"]
---

# unity-serialized-workflow

Canonical rules for editing Unity objects through the `SerializedObject` / `SerializedProperty` API. This skill is scoped to the lifecycle inside a `CustomEditor`, `PropertyDrawer`, or editor window inspector panel. Mutation outside that lifecycle belongs to `unity-undo-prefab-safety`.

## Scope and delegations

Covered here:

- `OnInspectorGUI` lifecycle: `Update` → `Find*` → GUI → `ApplyModifiedProperties`.
- `FindProperty` vs `FindPropertyRelative` vs `GetArrayElementAtIndex`.
- Multi-object editing.
- `EditorGUI.BeginChangeCheck` / `EndChangeCheck` and when it is the right hammer.
- `PropertyDrawer.OnGUI` with `BeginProperty` / `EndProperty`.
- `ISerializationCallbackReceiver`, `[SerializeReference]`, `OnValidate`.

Delegated elsewhere:

- **EditorWindow, CustomEditor skeleton, menu items** → `unity-editor-tooling`.
- **Asset batching and postprocessors** → `unity-assetdatabase-tools`.
- **Scene or prefab mutation outside the inspector** → `unity-undo-prefab-safety`.

## Canonical `OnInspectorGUI` skeleton

```csharp
[CustomEditor(typeof(Foo))]
public class FooEditor : Editor
{
    private SerializedProperty _name;
    private SerializedProperty _items;

    private void OnEnable()
    {
        _name  = serializedObject.FindProperty("_name");
        _items = serializedObject.FindProperty("_items");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(_name);
        EditorGUILayout.PropertyField(_items, includeChildren: true);

        serializedObject.ApplyModifiedProperties();
    }
}
```

The body starts with `Update()` and ends with `ApplyModifiedProperties()`. Everything between the two is GUI. Calling `Update()` mid-body overwrites unapplied edits; calling `ApplyModifiedProperties()` mid-body commits partial state.

Cache `SerializedProperty` handles in `OnEnable`; creating them every frame inside `OnInspectorGUI` is measurably slower and risks drift when the target changes.

## `FindProperty` vs `FindPropertyRelative`

- `serializedObject.FindProperty("_fieldName")` — absolute from the object root.
- `parentProperty.FindPropertyRelative("childField")` — relative to a property handle; used for nested structs and classes.
- `parentProperty.GetArrayElementAtIndex(i)` — element of an array or list property.

Serialized field names match the backing C# field names (including leading `_` or `m_`). When a field is renamed, add `[FormerlySerializedAs("oldName")]` to preserve asset compatibility.

## Multi-object editing

```csharp
var so = new SerializedObject(targets);  // plural: all selected targets
so.Update();
// edits apply to every target via ApplyModifiedProperties
so.ApplyModifiedProperties();
```

The `targets` array is populated when the user multi-selects objects with the same type. Property values that differ across the selection appear with a mixed-value indicator; reading `so.isEditingMultipleObjects` confirms the state.

## Property iteration

```csharp
var prop = serializedObject.GetIterator();
var enterChildren = true;
while (prop.NextVisible(enterChildren))
{
    enterChildren = false;
    EditorGUILayout.PropertyField(prop, includeChildren: true);
}
```

Important rules:

- Copy the iterator with `.Copy()` before recursing into children if you need a stable cursor.
- `NextVisible(true)` enters visible children once; subsequent calls should pass `false` to stay at the same nesting level.

## `BeginChangeCheck` / `EndChangeCheck`

Use the change-check block when you need to run logic ONLY if the user modified a value in the GUI pass.

```csharp
EditorGUI.BeginChangeCheck();
EditorGUILayout.PropertyField(_name);
if (EditorGUI.EndChangeCheck())
{
    OnNameChanged();              // custom side-effect
}
serializedObject.ApplyModifiedProperties();   // still required
```

`EndChangeCheck` detects that the GUI pass saw an interaction — it does NOT replace `ApplyModifiedProperties`. Always call both.

`ApplyModifiedPropertiesWithoutUndo()` exists for edits that should not appear in the undo stack (e.g., internal cache updates). Use it sparingly; the default `ApplyModifiedProperties` records undo automatically.

## `PropertyDrawer.OnGUI`

```csharp
[CustomPropertyDrawer(typeof(MyAttribute))]
public class MyDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        label = EditorGUI.BeginProperty(position, label, property);
        try
        {
            EditorGUI.PropertyField(position, property, label, includeChildren: true);
        }
        finally
        {
            EditorGUI.EndProperty();
        }
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        => EditorGUI.GetPropertyHeight(property, label, includeChildren: true);
}
```

- `BeginProperty` / `EndProperty` wrap every drawer so prefab-override highlighting works. Missing them breaks inspector chrome silently.
- Override `GetPropertyHeight` whenever the drawer renders more than a single row.

## `[SerializeField]` vs `[SerializeReference]` vs `ISerializationCallbackReceiver`

- `[SerializeField]` — default; serializes a concrete type by value. Fields must be public or have this attribute to appear in the inspector.
- `[SerializeReference]` — serializes a polymorphic reference; required for interface fields and abstract base classes. The referenced type must be `[Serializable]`.
- `ISerializationCallbackReceiver` — implement to customize the pre-serialize / post-deserialize step (e.g., round-trip a `Dictionary<TK, TV>` as two parallel lists).

## `OnValidate`

`OnValidate()` runs on script compile and on inspector edit. Use it for invariants that must hold whenever serialized data changes (clamping, ensuring non-null fallbacks). It runs on the `MonoBehaviour` / `ScriptableObject` instance after deserialization; setter-based invariants that bypass the serializer belong here.

## Good vs bad pattern pairs

**Bad: direct field mutation inside a CustomEditor**

```csharp
public override void OnInspectorGUI()
{
    var foo = (Foo)target;
    foo.value = EditorGUILayout.IntField("Value", foo.value); // bypasses undo + prefab overrides
}
```

**Good:**

```csharp
public override void OnInspectorGUI()
{
    serializedObject.Update();
    EditorGUILayout.PropertyField(serializedObject.FindProperty("_value"));
    serializedObject.ApplyModifiedProperties();
}
```

**Bad: missing `EndProperty`**

```csharp
public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
{
    EditorGUI.PropertyField(position, property); // prefab override styling broken
}
```

**Good:** see the `PropertyDrawer.OnGUI` template above.

## Further reading

- `_shared/anti-patterns.md` (items 11–13)
- `_shared/version-gates.md`
- `unity-editor-tooling/SKILL.md`
