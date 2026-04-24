# Checklist: AssetDatabase batch safety

Run through every item before merging code that calls `AssetDatabase.StartAssetEditing`, creates more than ten assets at once, or writes an `AssetPostprocessor`. Each bullet includes a verification command.

## Pre-batch

- [ ] A fresh `git status` shows no unrelated working-tree drift. Verify: `git status --porcelain`.
- [ ] Every code path that opens a batch has a paired close in a `finally`. Verify: `grep -n "StartAssetEditing" path/to/file.cs` and confirm the same file contains the matching `StopAssetEditing` inside a `finally` block within the same method.
- [ ] On Unity 6 or newer, prefer `using (new AssetDatabase.AssetEditingScope())` over manual Start/Stop. Verify: `grep -n "StartAssetEditing\|AssetEditingScope" .` and confirm the disposable form is used where `#if UNITY_6000_0_OR_NEWER` applies.
- [ ] Queries against `AssetDatabase.LoadAssetAtPath`, `FindAssets`, or `GUIDToAssetPath` happen *before* opening the batch. Verify manually by reading the method top-to-bottom.

## Mid-batch

- [ ] No `AssetDatabase.Refresh()` appears inside the loop. Verify: `grep -n "Refresh()" path/to/file.cs` and confirm calls are outside the loop or removed entirely.
- [ ] Progress bar (`EditorUtility.DisplayCancelableProgressBar`) is shown for any loop that iterates over more than ten assets. Verify by reading the loop body.
- [ ] The user's cancel signal (`EditorUtility.DisplayCancelableProgressBar` return value) aborts the batch cleanly, still calling `StopAssetEditing` in `finally`.

## Post-batch

- [ ] `StopAssetEditing` is the first statement of the `finally` block; `ClearProgressBar` is second. Verify by reading.
- [ ] A single `AssetDatabase.Refresh()` call occurs after the batch if new assets were written. Verify: `grep -c Refresh path/to/file.cs`.
- [ ] Any generated asset path is reported via `UvtLog.Info` (or the package's logger) so the user can locate it. Verify: `grep -n "Log\|Debug.Log" path/to/file.cs`.

## AssetPostprocessor-specific

- [ ] The postprocessor declares a static `HashSet<string>` recursion guard populated in `try` and cleared in `finally`. Verify: `grep -n "HashSet<string>" path/to/*Postprocessor.cs`.
- [ ] `GetPostprocessOrder()` returns an explicit integer, not the default `0`. Verify: `grep -n "GetPostprocessOrder" path/to/*Postprocessor.cs`.
- [ ] `GetVersion()` returns an integer that increments whenever the postprocessor's behavior changes. Verify by reading and comparing to the previous git SHA.
- [ ] No `AssetDatabase.ImportAsset` is called from inside the postprocessor without passing the path through the recursion guard.
- [ ] On Unity 2021.2+, the `OnPostprocessAllAssets` overload accepts `bool didDomainReload`. Verify: `grep -n "OnPostprocessAllAssets" path/to/*Postprocessor.cs`.

## Further reading

- `unity-assetdatabase-tools/SKILL.md`
- `_shared/anti-patterns.md` (items 7–10)
- `_shared/version-gates.md` (AssetEditingScope recipe)
