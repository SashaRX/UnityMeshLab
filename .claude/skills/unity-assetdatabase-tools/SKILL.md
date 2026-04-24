---
name: unity-assetdatabase-tools
description: ALWAYS invoke this skill when code touches AssetDatabase, AssetImporter, AssetPostprocessor, .meta files, or performs bulk asset creation/import/move/copy/delete. Do not use File.Move or File.Delete on anything under Assets/ or Packages/; do not call AssetDatabase.Refresh inside a loop; do not write an AssetPostprocessor without a static HashSet recursion guard. Mandatory — wrap batches in try/finally with StartAssetEditing and StopAssetEditing, or AssetEditingScope on Unity 6+.
paths: ["**/*AssetPostprocessor*.cs", "**/*Importer*.cs"]
---

# unity-assetdatabase-tools

Guidance for the Unity asset pipeline: batching bulk operations, moving assets while preserving GUIDs, and writing well-behaved `AssetPostprocessor` subclasses. Asset-pipeline concerns are orthogonal to mutation concerns — scene and prefab edits belong to `unity-undo-prefab-safety`.

## Scope and delegations

Covered here:

- `AssetDatabase.StartAssetEditing` / `StopAssetEditing` batching.
- `AssetDatabase.MoveAsset` / `RenameAsset` / `CopyAsset` / `DeleteAsset` semantics.
- `AssetImporter.userData` and per-importer configuration.
- `AssetPostprocessor` authoring — recursion guards, ordering, versioning.
- `OnPostprocessAllAssets` including the 2021.2+ `didDomainReload` overload.

Delegated elsewhere:

- **Scene or component mutation** → `unity-undo-prefab-safety`.
- **SerializedObject in a CustomEditor** → `unity-serialized-workflow`.
- **Package structure, asmdef placement** → `unity-package-architect`.

## The batching contract

`AssetDatabase.StartAssetEditing` is reference-counted. Every call must be paired with exactly one `StopAssetEditing` in a `finally` block. Unreleased counters leave the editor unresponsive — the import pipeline cannot drain while the counter is above zero. Queries between `Start` and `Stop` see pre-batch state; imports are deferred until `Stop`.

```csharp
AssetDatabase.StartAssetEditing();
try
{
    foreach (var path in paths)
        CreateOneAsset(path);
}
finally
{
    AssetDatabase.StopAssetEditing();
    EditorUtility.ClearProgressBar();
}
```

On Unity 6 or newer, prefer the disposable scope:

```csharp
#if UNITY_6000_0_OR_NEWER
using (new AssetDatabase.AssetEditingScope())
{
    foreach (var path in paths)
        CreateOneAsset(path);
}
#else
AssetDatabase.StartAssetEditing();
try { foreach (var path in paths) CreateOneAsset(path); }
finally { AssetDatabase.StopAssetEditing(); }
#endif
```

Do not call `AssetDatabase.LoadAssetAtPath`, `AssetDatabase.FindAssets`, or `AssetDatabase.GUIDToAssetPath` expecting up-to-date results while a batch is open — imports are deferred and queries see the pre-batch state. Gather inputs before the batch; persist outputs after it.

Do not call `StartAssetEditing` from `EditorApplication.update` without a same-tick `StopAssetEditing`; the unbalanced counter blocks the editor update cycle.

## Move, rename, copy, delete

Always use AssetDatabase APIs. Filesystem operations orphan `.meta` files and break GUID links.

| Operation | API |
|---|---|
| Move or rename | `AssetDatabase.MoveAsset(from, to)` returns `""` on success, error string otherwise |
| Rename in place | `AssetDatabase.RenameAsset(path, newName)` — cannot change file extension |
| Copy | `AssetDatabase.CopyAsset(from, to)` |
| Delete | `AssetDatabase.DeleteAsset(path)` — returns `bool`; also deletes `.meta` |

Always check `MoveAsset` and `CopyAsset` results. Never use `File.Move`, `File.Copy`, `File.Delete`, or `Directory.Delete` on anything under `Assets/` or `Packages/<local-package>/`.

Paths are relative to the project root, use forward slashes only, and must include the file extension.

## AssetPostprocessor authoring

A postprocessor is a class extending `AssetPostprocessor`. It is discovered automatically; Unity calls the declared `OnPreprocess*` / `OnPostprocess*` methods at the appropriate pipeline stage.

```csharp
internal sealed class Uv2AssetPostprocessor : AssetPostprocessor
{
    private static readonly HashSet<string> s_Guard = new HashSet<string>();

    public override int GetPostprocessOrder() => 1000;
    public override uint GetVersion() => 3; // increment on behavior change

    private void OnPreprocessModel()
    {
        if (!s_Guard.Add(assetPath)) return;
        try
        {
            var importer = (ModelImporter)assetImporter;
            ApplyRepoConventions(importer);
        }
        finally
        {
            s_Guard.Remove(assetPath);
        }
    }

#if UNITY_2021_2_OR_NEWER
    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted, string[] moved, string[] movedFromAssetPaths,
        bool didDomainReload)
    {
        if (didDomainReload) RebuildCaches();
    }
#else
    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted, string[] moved, string[] movedFromAssetPaths)
    {
        RebuildCaches();
    }
#endif
}
```

Rules:

- **`GetPostprocessOrder`** returns an explicit non-zero integer whenever order matters relative to other postprocessors; default `0` is ambiguous.
- **`GetVersion`** returns a monotonically-increasing integer; increment whenever the postprocessor's behavior changes so the import cache is invalidated.
- **Recursion guard** is a static `HashSet<string>` keyed on `assetPath`, populated in `try`, cleared in `finally`. Required for any postprocessor that calls `AssetDatabase.ImportAsset` or re-triggers import.
- **`OnPostprocessAllAssets`** is static (not instance) and does NOT participate in `GetPostprocessOrder`. For ordering across postprocessors at this stage, use assembly dependencies (asmdef references).
- **Ship postprocessors as DLLs in production**. A postprocessor with a compile error locks the asset pipeline — no asset imports, including the fix for the error. DLLs bypass the compilation cycle.

## Importer configuration

- `AssetImporter.userData` is a per-asset, JSON-friendly string blob. Use it for round-trip state that belongs to the importer, not to the asset. Example: the last successful import timestamp.
- `importer.SaveAndReimport()` flushes importer changes and re-triggers import. Prefer this over `AssetDatabase.ImportAsset` when the change came from user configuration.

## Good vs bad pattern pairs

**Bad: importing 10 000 textures without a batch**

```csharp
foreach (var guid in guids)
{
    var path = AssetDatabase.GUIDToAssetPath(guid);
    AssetDatabase.ImportAsset(path); // each call triggers a full refresh pass
}
AssetDatabase.Refresh();
```

Time complexity is O(N²) on the asset pipeline.

**Good: batched**

```csharp
AssetDatabase.StartAssetEditing();
try
{
    foreach (var guid in guids)
    {
        var path = AssetDatabase.GUIDToAssetPath(guid);
        AssetDatabase.ImportAsset(path);
    }
}
finally
{
    AssetDatabase.StopAssetEditing();
}
```

**Bad: `File.Move` then `AssetDatabase.Refresh`**

```csharp
File.Move(oldPath, newPath);
AssetDatabase.Refresh(); // .meta is orphaned; GUID link broken
```

**Good: `AssetDatabase.MoveAsset`**

```csharp
var err = AssetDatabase.MoveAsset(oldPath, newPath);
if (!string.IsNullOrEmpty(err)) throw new IOException(err);
```

## Further reading

- `_checklists/batch-safety.md`
- `_shared/version-gates.md`
- `_shared/anti-patterns.md`
