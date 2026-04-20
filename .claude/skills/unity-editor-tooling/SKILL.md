---
name: unity-editor-tooling
description: Write and review Unity Editor-only code — EditorWindow, CustomEditor, PropertyDrawer, menu items, IMGUI and UI Toolkit inspectors. Use when creating files under an Editor/ folder, writing [MenuItem], [CustomEditor], [CustomPropertyDrawer], [InitializeOnLoad], or any code inside an asmdef with includePlatforms Editor. Not for AssetDatabase batching or prefab mutation — delegate to unity-assetdatabase-tools and unity-undo-prefab-safety.
paths: ["**/Editor/**/*.cs"]
---

# unity-editor-tooling

Authoring and review guidance for Unity Editor-only code: EditorWindow, CustomEditor, PropertyDrawer, menu items, and initialization hooks. This skill is the umbrella entry point for editor surfaces; it delegates to siblings for asset-pipeline and mutation concerns.

## Scope and delegations

This skill covers:

- EditorWindow authoring (IMGUI and UI Toolkit).
- CustomEditor scaffolding (the serialized-property logic belongs to `unity-serialized-workflow`).
- PropertyDrawer and DecoratorDrawer.
- `[MenuItem]`, `[InitializeOnLoad]`, `[InitializeOnLoadMethod]`, `[OnOpenAsset]`, `AssetModificationProcessor`.

Delegated elsewhere:

- **AssetDatabase batching, import pipeline, `AssetPostprocessor`** → `unity-assetdatabase-tools`.
- **Any mutation of scene objects, components, prefabs, or prefab overrides** → `unity-undo-prefab-safety`.
- **`SerializedObject` / `SerializedProperty` lifecycle inside CustomEditor/PropertyDrawer** → `unity-serialized-workflow`.
- **asmdef and package layout** → `unity-package-architect`.

## Editor asmdef shape

Every editor asmdef sits under an `Editor/` folder and declares an Editor-only platform filter.

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
  "versionDefines": [],
  "noEngineReferences": false
}
```

Rules:

- `includePlatforms: ["Editor"]` and `excludePlatforms: []` — one is non-empty, the other empty.
- `autoReferenced: false` to keep the editor asmdef out of the default reference set of consumer assemblies.
- `name` equals the asmdef file basename, e.g., `SashaRX.UnityMeshLab.Editor.asmdef`.

## EditorWindow lifecycle

IMGUI and UI Toolkit entry points live side by side; choose one per window based on Unity version and preferred style.

```csharp
public class HealthWindow : EditorWindow
{
    [MenuItem("Tools/Prefab Doctor/Open Health Window")]
    public static void Open() => GetWindow<HealthWindow>("Prefab Health");

    private void OnEnable()    { /* acquire resources, subscribe */ }
    private void OnDisable()   { /* release resources, unsubscribe */ }
    private void OnInspectorUpdate() { Repaint(); }

#if UNITY_2022_2_OR_NEWER
    private void CreateGUI()   { /* UI Toolkit root; ignore OnGUI if used */ }
#endif

    private void OnGUI()       { /* IMGUI fallback */ }
}
```

- Acquire subscriptions in `OnEnable`, release in `OnDisable`. `EditorApplication.update`, `Undo.undoRedoPerformed`, `Selection.selectionChanged` are the common targets.
- Call `Repaint` from `OnInspectorUpdate` (runs 10× per second) instead of `EditorApplication.update`; this keeps UI lag bounded without burning frames.
- Use `CreateGUI` for UI Toolkit on 2022.2+. When both are defined, IMGUI `OnGUI` is ignored unless the visual element root is empty.

## CustomEditor skeleton

```csharp
[CustomEditor(typeof(PrefabHealthProfile))]
public class PrefabHealthProfileEditor : Editor
{
    private SerializedProperty _threshold;

    private void OnEnable()
    {
        _threshold = serializedObject.FindProperty("_threshold");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(_threshold);
        serializedObject.ApplyModifiedProperties();
    }
}
```

The inspector body must start with `Update()` and end with `ApplyModifiedProperties()`. Everything in between is GUI. For the property-manipulation details, defer to `unity-serialized-workflow`.

## PropertyDrawer vs DecoratorDrawer

- `PropertyDrawer` draws a single serialized property. Called once per instance of the decorated type.
- `DecoratorDrawer` draws without a target property — useful for section headers and separators inside an inspector. Called once per attribute occurrence.

Override `GetPropertyHeight` whenever the drawer changes vertical footprint; otherwise Unity clips subsequent rows.

## `[MenuItem]` conventions

- Menu root: `Tools/<DisplayName>/<Action>`; e.g., `Tools/Prefab Doctor/Open Health Window`.
- Shortcut syntax: `%` Ctrl/Cmd, `#` Shift, `&` Alt; e.g., `"Tools/Prefab Doctor/Refresh %#r"`.
- Priority integer controls grouping; keep related items within 10 of each other.
- Validation function: a sibling method with the same menu path, `validate: true`, returning `bool`:

```csharp
[MenuItem("Tools/Prefab Doctor/Refresh", validate = true)]
private static bool RefreshValidate() => Selection.activeGameObject != null;
```

## `[InitializeOnLoad]` and `[InitializeOnLoadMethod]`

- `[InitializeOnLoad]` on a class runs its static constructor on domain reload. Use for long-lived subscriptions.
- `[InitializeOnLoadMethod]` on a static method runs it on domain reload. Cheaper; prefer this when class-level state is not needed.
- Never perform blocking I/O from either hook — the editor stalls on domain reload.
- Log a single one-line message at INFO level so the user can confirm the hook fired.

## Good vs bad pattern pairs

**Bad: heavy work in `OnGUI`**

```csharp
private void OnGUI()
{
    _results = AssetDatabase.FindAssets("t:Prefab"); // runs every repaint
    foreach (var guid in _results) { /* … */ }
}
```

**Good: precompute on event, redraw on state**

```csharp
private string[] _results = Array.Empty<string>();

private void OnEnable()   { Selection.selectionChanged += Refresh; Refresh(); }
private void OnDisable()  { Selection.selectionChanged -= Refresh; }
private void Refresh()    { _results = AssetDatabase.FindAssets("t:Prefab"); Repaint(); }

private void OnGUI()      { foreach (var guid in _results) { /* … */ } }
```

**Bad: direct `Selection.activeObject` mutation inside `OnGUI`**

```csharp
private void OnGUI() { Selection.activeObject = _target; /* re-entrant paint */ }
```

**Good: gate on explicit user input**

```csharp
private void OnGUI()
{
    if (GUILayout.Button("Select")) Selection.activeObject = _target;
}
```

## Version gates

- Target the package's minimum Unity version declared in `package.json`.
- On 2021.3 LTS: IMGUI-first inspectors; UI Toolkit supported but limited.
- On 2022.2+: UI Toolkit inspectors (`CreateInspectorGUI`) are mature; prefer them for new code.
- Gate UI-Toolkit-only code with `#if UNITY_2022_2_OR_NEWER` and fall back to IMGUI in `#else`.

See `_shared/version-gates.md` for the canonical recipes.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `_shared/anti-patterns.md`
