# FBX Export Modernization Plan

Plan for next session. Baseline: PR #75 on `claude/fix-vertexao-shader-eljF1`
(already merged up through commit `93bd583`).

## Why

`LightmapTransferTool.ExportFbx(overwriteSource)` is destructive and opaque.
User clicks "Overwrite Source FBX", sees a one-line confirm dialog, and the
tool silently reshapes the FBX hierarchy, prunes stale children, normalizes
root transform, and commits. No preview, no rename, no awareness of prefabs
referencing the FBX elsewhere in the project.

Goal: add preview + rename + prefab sync as 3 independent PRs, respecting
both pipelines (sidecar on/off) and .meta file semantics.

## Current export flow (as of PR #75)

`LightmapTransferTool.cs:908-1284` `ExportFbx(bool overwriteSource)`:

1. Find source FBX path via `ctx.MeshEntries[*].fbxMesh` asset path or
   `PrefabUtility.GetCorrespondingObjectFromSource(ctx.LodGroup.gameObject)`.
2. `EditorUtility.DisplayDialog("Overwrite Source FBX", …)` — single
   confirm, no hierarchy visibility.
3. `File.Copy` creates `.fbx.bak` and `.fbx.meta.bak` side-by-side.
4. `tempRoot = Instantiate(fbxPrefab)` — clones the FBX asset prefab.
5. Selective mesh replacement by name: `meshReplacements[fbxMesh.name] = export`
   (lines 1039-1070). Unmatched entries append as new children.
6. Stale-child pruning (lines 1121-1126) — remove direct children not in
   `validNames`. Codex flagged this as P1 multiple times (handled but fragile).
7. `NormalizeExportHierarchy(tempRoot)` (line 1131, body at 1473):
   - Reset root `localPosition/Rotation/Scale` to identity (does NOT bake
     into children — Codex P2, resolved).
   - Rename direct child matching root name to `baseName_LOD0`.
   - Bake collision node transform offsets into vertices.
8. Inject collision meshes from sidecar (`GetCollisionMeshesFromSidecar`).
9. Strip collision meshes to `pos + tris + normals + tangents` (no UVs).
10. Trim `MeshRenderer.sharedMaterials` to `mesh.subMeshCount`.
11. `ModelExporter.ExportObjects(exportPath, new[] { tempRoot }, Binary)`.
12. Restore `.meta` from `.meta.bak`, delete `.bak` files.
13. `SaveSidecarForExport` + `Uv2AssetPostprocessor.PrepareImportSettings`
    **only when** `PostprocessorDefineManager.IsEnabled()`.
14. `AssetDatabase.Refresh()` — Unity reimports the overwritten FBX.

## Two pipelines

### Pipeline A — Sidecar ON (`LIGHTMAP_UV_TOOL_POSTPROCESSOR` define active)

- Overwrite writes **two artifacts**: UV2 baked into FBX + `_uv2data.asset`
  sidecar next to the FBX (path = `{dir}/{name}_uv2data.asset`, see
  `Uv2DataAsset.GetSidecarPath`).
- Sidecar holds UV2 arrays keyed by `meshName`, `MeshFingerprint`, optional
  collision entries (from `CollisionMeshTool`).
- `Uv2AssetPostprocessor.OnPostprocessModel` (order=10000) runs on every
  reimport and re-applies UV2 from sidecar. Safety net when third-party
  postprocessors (Bakery) regenerate UV2.
- `fbxOverwritePaths` set makes the postprocessor skip the immediate
  post-overwrite reimport (UV2 already in file).

### Pipeline B — Sidecar OFF

- Only UV2 baked into FBX. No sidecar created
  (`LightmapTransferTool.cs:1266` branch).
- Postprocessor code is `#if`'d out, nothing re-applies UV2 after reimport.
- Existing orphaned sidecars (e.g. from a previous Pipeline A session) are
  read only for collision data, never updated or deleted.

## Goals

1. **Preview** of the proposed new hierarchy before commit.
2. **Rename** of FBX file and/or root GameObject before export.
3. **Prefab sync** — update linked `.prefab` assets after the FBX reimports.

Each is independently useful. Implement as 3 PRs in order.

---

## PR #1 — Export Preview Window (read-only)

### Scope

- Extract `BuildExportTempRoot(sourceFbxPath, entries, out ExportPlan plan)`
  as a pure function returning `(GameObject tempRoot, ExportPlan plan)`.
  `ExportPlan` carries all metadata for the preview UI.
- Add `FbxExportPreviewWindow : EditorWindow` with:
  - **Header row:** pipeline indicator (`Sidecar ON` / `Sidecar OFF`) +
    source FBX path + sidecar fate row.
  - **Tree diff panel:** before (current FBX asset tree) vs after
    (`tempRoot`), with per-node badges `[NEW] / [REPLACED] / [PRUNED] /
    [RENAMED] / [TRANSFORM RESET]`.
  - **Stats rows:** verts/tris per mesh, material trim diffs, collision
    node list.
  - **Sidecar fate row (Pipeline A):**
    `_uv2data.asset [CREATE / UPDATE / PRESERVE-COLLISION-ONLY]` +
    fingerprint regeneration note.
  - **Warning row (Pipeline B):** if orphan sidecar exists at target path.
  - **Export / Cancel** buttons.
- Replace `EditorUtility.DisplayDialog("Overwrite Source FBX", …)` on
  `LightmapTransferTool.cs:983` with opening this window. Export button
  invokes the rest of the existing flow as-is.

### Files

- `Editor/Tools/LightmapTransferTool.cs` — extract + call site change.
- `Editor/Framework/FbxExportPreviewWindow.cs` — new, ~400-500 lines IMGUI.
- `Editor/Framework/FbxExportPlan.cs` — new plan struct / diff types.

### Risks

- `BuildExportTempRoot` must leave the scene/asset state clean on cancel
  (destroy temp GameObjects).
- `PostprocessorDefineManager.IsEnabled()` must be read at commit time,
  not preview-open time (user can toggle between).
- Stale preview data if user changes selection mid-flow — window should
  either refresh or close.

### Verification

1. Preview a flat LOD0/LOD1/LOD2 FBX — diff shows zero `[PRUNED]`.
2. Preview an FBX with nested pivot hierarchy — diff shows expected
   `[PRUNED]` warnings for the nested pivots (exposes current behavior).
3. Preview with Pipeline A — sidecar fate row shows `UPDATE` with
   fingerprint count.
4. Preview with Pipeline B — sidecar row shows `N/A` or `ORPHAN WARNING`.
5. Cancel button leaves no temp GameObjects in scene hierarchy.

---

## PR #2 — Rename support (FBX file + root)

### Scope

- Two text fields in the preview window:
  - **Target FBX path** — default `sourceFbxPath`, editable.
  - **Root GameObject name** — default `tempRoot.name`, editable.
- Propagate root name changes to autogenerated child names
  (`newBase_LOD0`, `newBase_COL`) via `UvToolContext.ExtractGroupKey`.
- Build a `Dictionary<string,string> renameMap` of `oldMeshName→newMeshName`
  and pass it into `SaveSidecarForExport` so sidecar entries are written
  with the new names that will actually land in the exported FBX.
- Handle FBX file rename via **`AssetDatabase.MoveAsset`** (preserves GUID
  and .meta).

### .meta handling (explicit)

Unity keeps the asset `.meta` file next to its asset. `.meta` holds the
GUID and importer settings. Any rename flow must preserve or knowingly
replace the `.meta` to avoid breaking scene/prefab references.

Options for the rename flow:

- **A) Rename in place (same folder, new name):**
  `AssetDatabase.RenameAsset(sourceFbxPath, newNameWithoutExt)` — moves
  both FBX and `.meta` atomically, GUID preserved. Scene references auto-
  update via GUID. **Recommended for "rename and overwrite".**
- **B) Move to new folder:** `AssetDatabase.MoveAsset(old, new)` — same
  semantics as A but cross-folder. Preserves GUID and `.meta`.
- **C) Save As (keep old, create new):** `ModelExporter.ExportObjects` at
  new path. Unity creates a fresh `.meta` with new GUID. Old FBX is
  untouched with its old `.meta`/GUID. Scene references remain on OLD FBX.
  Must manually copy `ModelImporter` settings from old to new (or accept
  defaults).

**DO NOT** use `File.Move` or `File.Copy` on `.fbx` + `.meta` directly —
that orphans one or generates a fresh GUID on next import, breaking refs.

Current backup logic `.fbx.bak` + `.fbx.meta.bak` works for same-path
overwrite (option A with no rename). For rename flows:

- Option A (rename in place): call `RenameAsset` first, then overwrite at
  new path. Backup `.meta.bak` at new path before overwrite.
- Option B (cross-folder move): call `MoveAsset` first, then overwrite.
- Option C (save as): export to new path, let Unity create fresh `.meta`.
  Then use `ModelImporter.ExtractInnerProps` or copy `ModelImporter`
  settings field-by-field (`isReadable`, `importNormals`,
  `materialImportMode`, `externalObjectMap`, etc.).

Sidecar `.asset` files have the same rules — each sidecar has its own
`.meta` with its own GUID. Use `AssetDatabase.RenameAsset` /
`AssetDatabase.MoveAsset` / `AssetDatabase.CopyAsset` accordingly.

### Sidecar path cascade (Pipeline A)

When user renames the FBX filename:

- **Move sidecar to new path** (recommended when old FBX is being
  overwritten): `AssetDatabase.MoveAsset(oldSidecarPath, newSidecarPath)`.
- **Copy sidecar to new path** (keep both FBX copies working):
  `AssetDatabase.CopyAsset`.
- **Create fresh sidecar at new path** (clean slate): delete old, write
  new via `SaveSidecarForExport`.
- **Leave orphaned** (rarely desired): warn in UI.

`Uv2AssetPostprocessor.managedImportPaths` registration must happen against
the **new** FBX path (line 1269), not the old one.

### Scene reference rewire

Before `RenameAsset`, scan scene for `MeshCollider.sharedMesh` /
`MeshFilter.sharedMesh` referencing FBX sub-asset meshes whose names are
in the renameMap. Emit a warning count in the preview — Unity's
`RenameAsset` handles the **FBX GUID** level automatically, but sub-asset
meshes are looked up by `name` inside the asset, so if we rename a mesh
that a scene `MeshCollider` points to, the reference can drift.

Safer: also run a scene-wide mesh ref patcher after export for every entry
in `renameMap`.

### Files

- `Editor/Tools/LightmapTransferTool.cs` — accept `ExportPlan` with
  rename fields; thread `renameMap` through `SaveSidecarForExport` and
  collision sidecar saver.
- `Editor/Framework/FbxExportPreviewWindow.cs` — two text fields, rename
  diff row, sidecar fate radio buttons.
- `Editor/Uv2AssetPostprocessor.cs` — unchanged API; confirm
  `managedImportPaths` is keyed by new path.

### Risks

- Two FBX files with same base name in different folders have distinct
  sidecars. Renaming can accidentally write sidecar over a neighbor's
  sidecar — preview must show existing target sidecar path and abort on
  collision.
- `renameMap` for mesh names must match exactly what `NormalizeExport-
  Hierarchy` produces — no drift between preview and actual export.
- `fbxOverwritePaths`, `managedImportPaths`, `bypassPaths` in
  `Uv2AssetPostprocessor` are keyed by path — rename must update every
  set. Each has its own lifetime (bypass is immediate, managed is
  persistent).
- `ModelImporter` settings for Option C (Save As) — field-by-field copy
  is error-prone. Consider serializing via `SerializedObject` instead.

### Verification

1. Rename in place (same folder, new name): scene refs survive, sidecar
   moves with FBX.
2. Cross-folder move: same as above.
3. Save As copy: both FBX files exist, old one unchanged, new one has
   fresh sidecar with fresh fingerprints.
4. Sidecar collision override: new sidecar with old collision entries
   cascades correctly.
5. Scene `MeshCollider` with renamed mesh: warning shown, patch applied.

---

## PR #3 — Prefab sync

### Scope

- After `AssetDatabase.Refresh()` completes reimport (and, for Pipeline A,
  after the postprocessor re-applies UV2), walk dependent `.prefab` files
  and re-wire mesh/material/LODGroup references.
- Discovery via `AssetDatabase.GetDependencies(sourceFbxPath, recursive=true)`
  plus reverse-lookup `AssetDatabase.FindAssets("t:Prefab")` + per-prefab
  `GetDependencies` check.
- Preview window adds a list of target prefabs with per-prefab checkbox.
- For each enabled prefab:
  1. `PrefabUtility.LoadPrefabContents(prefabPath)` — unpacked copy.
  2. Walk tree. For each child GameObject matching the rename map
     (or the FBX root GameObject), re-assign `MeshFilter.sharedMesh` /
     `MeshCollider.sharedMesh` / `MeshRenderer.sharedMaterials` /
     `LODGroup` LOD renderers to the new FBX sub-asset refs.
  3. `PrefabUtility.SaveAsPrefabAsset(root, prefabPath)`.
  4. `PrefabUtility.UnloadPrefabContents(root)`.

### Pipeline-specific ordering

- **Pipeline A:** defer prefab sync via `EditorApplication.delayCall` so
  it runs after `Uv2AssetPostprocessor.OnPostprocessModel` has re-applied
  UV2. Otherwise the sync writes refs while sidecar re-apply is mid-flight.
- **Pipeline B:** run sync immediately after `AssetDatabase.Refresh()`.

### Scene variant prefabs vs standalone prefabs

- **Case A: direct FBX instance** — auto-syncs on reimport, nothing to do.
- **Case B: `.prefab` variant based on FBX** — inherits from FBX via
  PrefabUtility. Auto-syncs mesh refs. Overrides on renamed nodes may
  drift; emit a warning listing affected override paths.
- **Case C: standalone `.prefab` copied from FBX** — independent asset,
  no auto-sync. This is the primary target of PR #3. Rewire required.

### Files

- `Editor/Tools/LightmapTransferTool.cs` — call prefab sync after
  reimport.
- `Editor/Framework/FbxExportPrefabSync.cs` — new, discovery + rewire.
- `Editor/Framework/FbxExportPreviewWindow.cs` — prefab checklist UI.

### Risks

- Cost of scanning every `.prefab` in project — scope to scene
  dependencies first, optionally project-wide.
- Rename map must be applied to both the prefab GameObject tree AND the
  mesh sub-asset references. Order matters — rename GameObjects first,
  then re-point meshes.
- Component values (MeshCollider `convex`, LODGroup transition heights)
  must be preserved. Only mesh/material assignments are re-pointed.
- Unpacked prefab variants lose their inheritance — detect via
  `PrefabUtility.IsOutermostPrefabInstanceRoot` and skip / warn.

### Verification

1. Case C prefab with old mesh names: rewired correctly after export.
2. Case B variant: auto-syncs without our intervention, no drift on
   non-renamed children.
3. Case B variant with override on a renamed child: warning shown.
4. Pipeline A + Case C: sync defers until after postprocessor re-applies
   UV2, final prefab has correct UV2.
5. Project-wide scan: tool finds all prefabs referencing the renamed FBX.

---

## Cross-cutting risks

- `LIGHTMAP_UV_TOOL_POSTPROCESSOR` define toggled between preview open
  and commit — read at commit time in `ExportPlan.Commit()`.
- Third-party postprocessor order (Bakery is `order=1000`, ours is
  `order=10000`). Our sidecar re-apply always runs last. Don't break
  this contract.
- `Uv2AssetPostprocessor.bypassPaths` is a `HashSet` that clears
  per-session. Long-lived plans must re-add paths right before reimport.
- `Uv2DataAsset.toolSettings` is per-asset, not per-mesh. Rename doesn't
  affect it directly.

## Not in scope

- UV2 schema migration (handled by `Uv2DataAsset.schemaVersion`).
- `meshOptimizationFlags` handling (already done in PR #75).
- Collision mesh format changes (separate concern).
- Batch export of multiple FBX files at once (possible future extension).

## Verification matrix

| Scenario                              | Pipeline A | Pipeline B |
|---------------------------------------|-----------|-----------|
| Preview, cancel                       | no side effect | no side effect |
| Rename in place + overwrite           | sidecar moves | n/a |
| Rename in place, Pipeline A → B       | warn orphan | - |
| Save As copy                          | new sidecar | new FBX only |
| Prefab sync Case C                    | deferred   | immediate |
| Prefab sync Case B variant            | auto-sync + warn | auto-sync + warn |
| Sub-asset mesh rename in scene refs   | rewire scene | rewire scene |

## Entry points for next session

- Start with `Editor/Tools/LightmapTransferTool.cs:908` — `ExportFbx`.
- Read `Editor/Framework/UvToolHub.cs:769` for pipeline toggle UI.
- Read `Editor/Uv2AssetPostprocessor.cs:61` for `PrepareImportSettings`.
- Read `Editor/Uv2DataAsset.cs:411` for sidecar path conventions.
- PR #75 `claude/fix-vertexao-shader-eljF1` is the baseline branch.
