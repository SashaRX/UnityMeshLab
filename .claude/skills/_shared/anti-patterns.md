# Anti-patterns (canonical)

Every entry is a symptom, a root cause, and a fix. This file is referenced as Level-3 material by every skill in this directory. Keep entries short; deep-dive explanations belong in the relevant SKILL.md.

## Undo and prefab pitfalls

**1. `Undo.RecordObject` on reparenting silently fails.**
Root cause: `RecordObject` only records serialized property deltas; transform parent changes require `RegisterFullObjectHierarchyUndo` or `Undo.SetTransformParent`.
Fix: use `Undo.SetTransformParent(child, newParent, "Reparent")` for parenting operations.

**2. `AddComponent<T>()` recorded with `RecordObject` is lost on undo.**
Root cause: `RecordObject` does not capture component creation.
Fix: use `Undo.AddComponent<T>(gameObject)`; it records the creation atomically.

**3. Prefab instance override changes are not serialized.**
Root cause: `RecordObject` alone marks the object dirty but does not mark the override on the prefab instance.
Fix: call `Undo.RecordObject(obj, name)` first, mutate, then `PrefabUtility.RecordPrefabInstancePropertyModifications(obj)` — both calls, in that order, for every instance edit.

**4. `DestroyImmediate` inside a batch is unundoable.**
Root cause: `Object.DestroyImmediate` bypasses the undo system.
Fix: use `Undo.DestroyObjectImmediate(obj)` inside the editor; reserve raw `DestroyImmediate` for EditorWindow teardown or asset cleanup where undo does not apply.

**5. `RegisterCreatedObjectUndo` called after subsequent `RecordObject` entries loses the prior state.**
Root cause: the create-undo entry is inserted at the current group head; later `RecordObject` entries are orphaned across the group boundary.
Fix: call `Undo.RegisterCreatedObjectUndo(obj, name)` immediately after creation, before any further modification.

## AssetDatabase pitfalls

**6. `File.Move` / `File.Delete` on a Unity asset orphans the `.meta` file and breaks GUID links.**
Root cause: GUID-to-path resolution uses the sibling `.meta` file, which direct filesystem ops leave behind.
Fix: always use `AssetDatabase.MoveAsset`, `AssetDatabase.RenameAsset`, `AssetDatabase.CopyAsset`, `AssetDatabase.DeleteAsset`.

**7. `StartAssetEditing` without paired `StopAssetEditing` locks the editor.**
Root cause: the counter is reference-counted; unreleased increments prevent the asset pipeline from draining.
Fix: wrap every `StartAssetEditing` in `try/finally`; on Unity 6 prefer `using (new AssetDatabase.AssetEditingScope())`.

**8. Querying `AssetDatabase.LoadAssetAtPath` between Start/Stop returns stale or null.**
Root cause: imports are deferred until `StopAssetEditing`; query APIs see pre-batch state.
Fix: do all queries before entering the batch, or split the batch around the query.

**9. `AssetDatabase.Refresh()` inside a loop.**
Root cause: `Refresh` triggers a full import scan; nesting multiplies the cost quadratically.
Fix: call `Refresh` once after the loop, or wrap the loop in `StartAssetEditing`/`StopAssetEditing`.

**10. `AssetPostprocessor` without a recursion guard.**
Root cause: the postprocessor can cause re-imports that re-enter itself.
Fix: use a static `HashSet<string>` keyed on asset path, populated in `try`, cleared in `finally`.

## Serialization pitfalls

**11. `serializedObject.Update()` called mid-modification discards unapplied changes.**
Root cause: `Update()` copies the target back into the serialized object, overwriting in-progress edits.
Fix: call `Update()` exactly once at the top of `OnInspectorGUI` and `ApplyModifiedProperties()` exactly once at the bottom.

**12. `ApplyModifiedProperties` forgotten.**
Root cause: serialized property writes remain in the in-memory SerializedObject but never reach the target.
Fix: end every `OnInspectorGUI` and every `PropertyDrawer.OnGUI` with `ApplyModifiedProperties()`, or `EndProperty()` for drawers.

**13. Direct mutation of `target` / `targets` inside a CustomEditor.**
Root cause: direct field writes bypass undo and prefab override recording.
Fix: always edit via `serializedObject.FindProperty(...)` and `ApplyModifiedProperties()`.

**14. `MeshFilter.mesh` in edit mode leaks a cloned mesh every access.**
Root cause: `mesh` auto-clones the shared asset for the caller; the clone is never destroyed.
Fix: use `sharedMesh` in editor code; if you must clone, capture the result, use it, then `Object.DestroyImmediate(clone)` when done.

**15. Editing `sharedMesh` mutates the asset for all instances.**
Root cause: `sharedMesh` is the authored asset; modifications persist across the project.
Fix: `var clone = Object.Instantiate(filter.sharedMesh); clone.name = "…_Edit"; // edit clone`.

## Packaging pitfalls

**16. `Samples/` without a trailing tilde ships into consumer compilation.**
Root cause: folders without `~` suffix are imported by AssetDatabase into the consumer project.
Fix: rename to `Samples~` and declare entries in the `samples` array of `package.json`.

**17. Asmdef with both `includePlatforms` and `excludePlatforms` populated.**
Root cause: Unity requires exactly one of the two to be non-empty; the other must be `[]`.
Fix: pick one and clear the other.

**18. Editor asmdef referenced from a Runtime asmdef.**
Root cause: Runtime code compiled for standalone player cannot resolve Editor symbols.
Fix: move the shared code to a third Runtime asmdef that both reference, or gate Editor-only members with `#if UNITY_EDITOR` inside a Runtime-safe file.

**19. `"unity": "2022.3"` bump without updating CI matrix.**
Root cause: CI still validates against an unreachable minimum.
Fix: update the GameCI matrix and regenerate license activation for the new minimum in the same PR.

**20. Missing `.meta` files in git.**
Root cause: `.gitignore` excludes `.meta` patterns inadvertently.
Fix: always commit every `.meta` file next to its asset; audit `.gitignore` for broad patterns like `*.meta`.

## Skill-authoring pitfalls

**21. Description shorter than 80 characters.**
Root cause: terse descriptions undertrigger the skill.
Fix: include what the skill does plus a `Use when …` clause naming file extensions, API names, and domain nouns.

**22. First-person or second-person voice in description (`I can …`, `You should …`).**
Root cause: violates Anthropic's frontmatter best practice for skill descriptions.
Fix: rewrite in third person imperative (`Processes …`, `Audits …`).

**23. Frontmatter keys outside the allowed set (`version`, `keywords`, `tags`, `author`).**
Root cause: Claude.ai rejects unknown keys at validation time.
Fix: keep frontmatter to `name`, `description`, `license`, `allowed-tools`, `metadata`, plus Claude Code extensions (`when_to_use`, `paths`, `model`, `effort`, `argument-hint`, `disable-model-invocation`, `user-invocable`, `context`, `agent`, `hooks`, `shell`).

**24. SKILL.md body over 500 lines.**
Root cause: Anthropic's 5,000-token Level-2 budget is exceeded; auto-compaction truncates the body.
Fix: split overflow content into `references/` files and link from SKILL.md one level deep.

**25. Windows backslash paths in skill text.**
Root cause: breaks path resolution on Linux/macOS CI and in `raw.githubusercontent.com` URLs.
Fix: always use forward slashes; add a CI check that greps for `\\[A-Za-z]` patterns inside `.claude/skills/**`.

**26. Prettier reflows single-line YAML descriptions into folded scalars.**
Root cause: Prettier wraps long YAML string values; the parser then reads a truncated prefix.
Fix: add `.claude/skills/**/SKILL.md` to `.prettierignore` or pin each description with `# prettier-ignore`.

**27. Time-sensitive language (`Before August 2025 use old API`).**
Root cause: the statement goes stale; Claude keeps citing outdated guidance.
Fix: move version-scoped text into collapsible `<details>` blocks keyed by Unity version or package version.

## Further reading

- `_shared/naming-conventions.md`
- `_shared/version-gates.md`
- `_checklists/undo-safety.md`, `_checklists/prefab-safety.md`, `_checklists/batch-safety.md`, `_checklists/package-release.md`
