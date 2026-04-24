# Overhauling `.claude/skills/` across three Unity UPM repositories: a paste-ready execution plan

**TL;DR**: Your current `.claude/skills/` directory has the right bones but is skeletal content-wise, its `_shared/` and `_checklists/` folders are orphaned (no SKILL.md references them, so progressive disclosure is broken), skill descriptions are too short and terse to trigger reliably, and there is an unresolved namespace convention conflict (`Company.PackageName` vs. bare `LightmapUvTool`). The fix is a five-phase plan per repo: snapshot, restructure, rewrite English SKILL.md bodies under 500 lines with pushy third-person descriptions, wire up `_shared/` and `_checklists/` as Level-3 resources with one-level-deep references, resolve the namespace conflict by adopting `SashaRX.<Package>` as the canonical two-segment rule, and add three missing skills (`unity-ci-validation`, `unity-package-bootstrap`, and a per-repo `repo-conventions`). GitHub fetchability was blocked in this session, so the plan is parameterized by per-repo variables and ships with a Phase 0 discovery script that auto-fills them.

---

## Part 1 — Research synthesis

### 1.1 Anthropic canonical rules for skill authoring

All quantitative claims below are verbatim from Anthropic canonical sources, with URLs.

**Frontmatter schema** (from https://docs.claude.com/en/docs/agents-and-tools/agent-skills/overview and https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices):

| Field | Rule | Source |
|---|---|---|
| `name` | Max 64 chars, lowercase letters/numbers/hyphens only, no XML, no reserved words `anthropic`/`claude` | Overview |
| `description` | Non-empty, max **1024 chars**, no XML | Overview |
| Point of view | **Third person**. "Processes Excel files and generates reports." Avoid "I can help you…" / "You can use this to…" | Best Practices |
| Claude Code budget | `description` + `when_to_use` truncated at **1,536 chars** in the listing; default budget is 1% of context or 8,000 chars fallback | https://code.claude.com/docs/en/skills |
| Claude Code optional fields | `when_to_use`, `allowed-tools`, `model`, `effort`, `paths`, `argument-hint`, `disable-model-invocation`, `user-invocable`, `context`, `agent`, `hooks`, `shell` | Claude Code docs |

**Progressive disclosure — three levels** (verbatim, Overview):

| Level | When loaded | Token cost | Content |
|---|---|---|---|
| 1 Metadata | Always at startup | ~100 tokens/skill | `name` + `description` from YAML |
| 2 Instructions | When triggered | Under 5,000 tokens | SKILL.md body |
| 3 Resources | As needed | Effectively unlimited | Bundled files, loaded only when referenced |

**Body length** (Best Practices, verbatim): "Keep SKILL.md body under 500 lines for optimal performance. If your content exceeds this, split it into separate files using the progressive disclosure patterns described earlier."

**Reference depth** (Best Practices, verbatim): "Keep references one level deep from SKILL.md. All reference files should link directly from SKILL.md to ensure Claude reads complete files when needed." Files longer than 100 lines must have a table of contents.

**Bundled-resource folder conventions** (verbatim from anthropics/skills skill-creator):

```
skill-name/
├── SKILL.md (required)
├── scripts/     - Executable code for deterministic/repetitive tasks
├── references/  - Docs loaded into context as needed
└── assets/      - Files used in output (templates, icons, fonts)
```

**Pushy descriptions** — Anthropic explicitly acknowledges the undertriggering problem. Verbatim from skill-creator SKILL.md at https://github.com/anthropics/skills/blob/main/skills/skill-creator/SKILL.md: *"Currently Claude has a tendency to 'undertrigger' skills — to not use them when they'd be useful. To combat this, please make the skill descriptions a little bit 'pushy'."* The canonical template is: what the skill does + **"Use when …"** + explicit trigger nouns/verbs/file types, in third person, on a single YAML line.

**Canonical examples** (from public docs):

- `description: Extract text and tables from PDF files, fill forms, merge documents. Use when working with PDF files or when the user mentions PDFs, forms, or document extraction.`
- `description: Analyze Excel spreadsheets, create pivot tables, generate charts. Use when analyzing Excel files, spreadsheets, tabular data, or .xlsx files.`
- `description: Generate descriptive commit messages by analyzing git diffs. Use when the user asks for help writing commit messages or reviewing staged changes.`

**Filesystem precedence** (Claude Code, verbatim): "When skills share the same name across levels, higher-priority locations win: **enterprise > personal > project**." Project skills live at `.claude/skills/<name>/SKILL.md`. Claude Code watches the directory and reloads within the session; creating a new top-level skills directory mid-session requires restart.

**Auto-compaction** (Claude Code): re-attaches the most recent invocation of each skill, keeping the first **5,000 tokens**, with a combined **25,000-token** budget. Implication: standing instructions should be self-sufficient in the first 5k tokens.

**Anti-patterns verified from Anthropic sources**:
- Vague names (`helper`, `utils`, `tools`) or vague descriptions (`Helps with documents`).
- Time-sensitive prose ("Before August 2025 use old API") — use `<details>` blocks instead.
- Windows backslash paths — "Always use forward slashes."
- Multiple competing approaches — "Don't present multiple approaches unless necessary."
- Heavy-handed ALL-CAPS MUSTs — "reframe and explain the reasoning."
- Deeply nested references — Claude may partial-read with `head -100` and miss content.

**Practitioner pitfalls worth catching in CI**:

- Jesse Vincent (fsck.com, 2025-12-17): the default `SLASH_COMMAND_TOOL_CHAR_BUDGET` is 15,000 chars in Claude Code 2.0.70; overflow is silent and the system prompt instructs Claude *never* to use unlisted skills.
- Scott Spence: Prettier reflows single-line YAML descriptions into folded scalars, which the parser can reject — pin with `# prettier-ignore` or add YAML to Prettier's ignore file.
- Ivan Seleznov's 650-trial A/B: passive "Use when…" phrasing averages ~77% activation; directive "ALWAYS invoke this skill when … Do not X directly" reaches ~100% — but warns about *directive saturation* if every skill uses it. Recommendation: directive form for the three or four most-critical skills, third-person "Use when…" for the rest.
- Anthropic's own GitHub issues #37, #202, #249 flag that frontmatter keys outside the spec (`version`, `keywords`, `tags`, `author`) break validation on Claude.ai. Keep frontmatter to `name`, `description`, `license` (optional), `allowed-tools` (optional), `metadata` (optional), plus Claude Code extensions where needed.

### 1.2 Cross-tool Unity AI rules research

Six recurring Unity topics appear across Cursor (`.cursorrules` legacy, `.cursor/rules/*.mdc` modern), Windsurf (`.windsurfrules`), Copilot (`.github/copilot-instructions.md`), Aider (`CONVENTIONS.md`), and Cline (`.clinerules/`):

| Theme | What consistently appears | Translates to |
|---|---|---|
| Editor mutations | `Undo.RecordObject`, `Undo.RegisterCreatedObjectUndo`, `EditorUtility.SetDirty`, `PrefabUtility.RecordPrefabInstancePropertyModifications`, `AssetDatabase.Refresh` | `unity-undo-prefab-safety` + `unity-assetdatabase-tools` (keep both; orthogonal) |
| UPM layout | `package.json`, asmdef boundaries, Runtime/Editor/Tests split, `Samples~`, `Documentation~` | `unity-package-architect` |
| Serialization | `[SerializeField]`, `[SerializeReference]`, `ISerializationCallbackReceiver`, prefab overrides, `.meta`/GUID | `unity-serialized-workflow` |
| Editor tooling | `EditorWindow`, `CustomEditor`, `PropertyDrawer`, `[MenuItem]`, `[InitializeOnLoad]` | `unity-editor-tooling` |
| Runtime performance | `GetComponent` caching, coroutines, Jobs/Burst, pooling, profiler markers | Out of scope for these three repos (editor tools). Defer. |
| Naming/style | `m_` prefix, PascalCase, `#region Unity Lifecycle`, `Author.Package` namespaces | Merge into `_shared/naming-conventions.md` + per-repo `repo-conventions` skill |

**Structural lesson** from Cursor's `.cursor/rules/*.mdc` trend and Windsurf orientation documents: authors have moved from monolithic always-on rules to **many short rules with tight scoping** (globs, file patterns, directive descriptions). Claude Code skills already support this natively via the `paths` frontmatter field and description-based routing. Use `paths` on skills that are tied to file patterns (e.g. `paths: ["**/package.json", "**/*.asmdef"]` on `unity-package-architect`).

**The Undo/SetDirty/AssetDatabase cluster is the single most-repeated editor topic across all surveyed tools** (Windsurf's CoderGamester/mcp-unity rules, Cursor's phucthai97/CursorRule_Unity, etc.). It deserves dedicated progressive-disclosure treatment — which is exactly what `unity-undo-prefab-safety` should do.

**Unity trigger vocabulary that recurred in ≥3 tool corpora** (use these in descriptions and `paths`):
- File extensions: `.cs`, `.asmdef`, `.asmref`, `.prefab`, `.unity`, `.asset`, `.meta`, `package.json`
- Folders: `Assets/`, `Packages/`, `Runtime/`, `Editor/`, `Tests/`, `Samples~/`, `Documentation~/`
- APIs: `MonoBehaviour`, `ScriptableObject`, `EditorWindow`, `Editor`, `PropertyDrawer`, `AssetPostprocessor`, `AssetDatabase`, `EditorUtility.SetDirty`, `Undo.RecordObject`, `PrefabUtility`, `SerializedObject`/`SerializedProperty`, `PrefabStage`
- Attributes: `[SerializeField]`, `[SerializeReference]`, `[CustomEditor]`, `[CustomPropertyDrawer]`, `[MenuItem]`, `[InitializeOnLoad]`, `[ContextMenu]`, `[CreateAssetMenu]`

### 1.3 Unity Editor API verification (2021.3 → 2022.3 → Unity 6)

Each claim below has a Unity docs URL; use these directly in `_shared/` and skill bodies as authoritative references.

**AssetDatabase batching** (https://docs.unity3d.com/ScriptReference/AssetDatabase.StartAssetEditing.html):
- Calls are reference-counted; must be paired in `try/finally`. Unreleased counter = unresponsive editor.
- Assets created between Start/Stop are **not fully imported** until `StopAssetEditing` returns — APIs against them mid-batch may misbehave.
- **Unity 6 adds `AssetDatabase.AssetEditingScope`** (IDisposable, usable in a `using` block). Use `#if UNITY_6000_0_OR_NEWER`.
- Never call `StartAssetEditing` from `EditorApplication.update` without a same-tick `StopAssetEditing` — breaks the editor update cycle (https://issuetracker.unity3d.com/issues/assetdatabase-dot-startassetediting-inside-editorapplication-dot-update-without-assetdatabase-dot-stopassetediting-breaks-the-editor).

**AssetPostprocessor** (https://docs.unity3d.com/ScriptReference/AssetPostprocessor.html):
- `GetPostprocessOrder()` applies to per-asset callbacks, **not** to `OnPostprocessAllAssets`. For that, use assembly dependency attributes.
- Unity 2021.2+ adds `didDomainReload` overload to `OnPostprocessAllAssets`.
- Increment `GetVersion()` when behavior changes to invalidate import cache.
- Recursion guard: static `HashSet<string>` of paths currently being processed, `try/finally` remove.

**Prefab editing** (https://docs.unity3d.com/ScriptReference/PrefabUtility.LoadPrefabContents.html):
- Preferred API since 2020.1: `using (var scope = new PrefabUtility.EditPrefabContentsScope(path)) { ... }`.
- `PrefabStage` namespace moved from `UnityEditor.Experimental.SceneManagement` to `UnityEditor.SceneManagement` in 2021+; gate with `#if UNITY_2021_2_OR_NEWER`.
- For instance edits: `Undo.RecordObject(obj, name)` then `PrefabUtility.RecordPrefabInstancePropertyModifications(obj)` — **in that order, always both**.

**Undo system** (https://docs.unity3d.com/ScriptReference/Undo.html):
- `Undo.RecordObject` does NOT capture parenting, `AddComponent`, or destruction — use `RegisterCompleteObjectUndo`, `Undo.AddComponent<T>`, `Undo.DestroyObjectImmediate` respectively.
- `Undo.RegisterCreatedObjectUndo` must be called **after creation, before modifications**, to avoid losing subsequent `RecordObject` entries.
- Grouping: `IncrementCurrentGroup()` → work → `SetCurrentGroupName(name)` → `CollapseUndoOperations(groupIndex)`.

**SerializedObject** (https://docs.unity3d.com/ScriptReference/SerializedObject.html):
- `Update()` at start of `OnInspectorGUI`; `ApplyModifiedProperties()` at end (records undo + SetDirty automatically).
- Never call `Update()` mid-modification — it discards unapplied changes.
- `EditorGUI.BeginChangeCheck()`/`EndChangeCheck()` detects GUI interaction, not property change — always pair with `ApplyModifiedProperties`.

**Mesh safety** (https://docs.unity3d.com/ScriptReference/MeshFilter-sharedMesh.html):
- `MeshFilter.mesh` in edit mode clones and leaks; Unity warns. Use `sharedMesh`.
- Editing `sharedMesh` mutates the asset for **all instances** and is not undoable by `Undo.RecordObject` alone — clone first with `Instantiate(meshFilter.sharedMesh)`.
- `Mesh.UploadMeshData(true)` frees CPU-side copy (non-readable after).

**Asmdef** (https://docs.unity3d.com/6000.1/Documentation/Manual/assembly-definition-file-format.html):
- `includePlatforms` and `excludePlatforms` are mutually exclusive — one must be empty.
- Editor-only: `"includePlatforms": ["Editor"]`.
- `versionDefines` expression uses interval notation: `[1.7,2.4.1]` or bare `7.1.0` (≥). Targets: package name, module name, or `"Unity"`.
- `UNITY_X_Y_OR_NEWER` defines exist since 5.3.4 but have **no patch-level granularity**; use `versionDefines` with `"name": "Unity"` if patch-level is needed.

**UPM package.json** (https://docs.unity3d.com/Manual/upm-manifestPkg.html):
- `name` must be reverse-DNS and match folder name.
- `unity: "2021.3"` targets a LTS minor; `unityRelease: "0b5"` optionally narrows.
- `samples` array points to `Samples~/Folder` subpaths; UPM copies into `Assets/Samples/` on import.

**Tilde-hidden folders** (https://docs.unity3d.com/Manual/cus-layout.html): any folder ending in `~` (or starting with `.`) is ignored by AssetDatabase — no `.meta` generated. Canonical use: `Samples~`, `Documentation~`, `Tests~` (if you ship tests but don't want them compiled in consumer projects).

**.meta and file operations** (https://docs.unity3d.com/Manual/AssetMetadata.html):
- Always use `AssetDatabase.MoveAsset`/`RenameAsset`/`CopyAsset` — direct filesystem ops break GUID links.
- `AssetDatabase.MoveAsset` returns `""` on success, error string otherwise.
- `RenameAsset` cannot change extension.
- Always commit `.meta` files to git.

**CI / batch mode** (https://docs.unity3d.com/Packages/com.unity.test-framework@2.0/manual/reference-command-line.html):
- `-batchmode -nographics -quit -projectPath <path> -logFile <path> -executeMethod Ns.Class.Method`.
- Test Runner: `-runTests -testPlatform EditMode|PlayMode -testResults <path.xml>`.
- Detect in code: `Application.isBatchMode`.

### 1.4 Three-repo status report

GitHub fetchability was blocked in this session at tool level, so the following is **partial** and must be completed by Phase 0's discovery script. What IS verified:

| Repo | Canonical casing | Confirmed to exist | Language | Role |
|---|---|---|---|---|
| PrefabDoctor | `PrefabDoctor` (PascalCase) | ✅ pinned on profile | C# | "Unity Editor tool: nested prefab override conflicts, project-wide prefab health scanner" |
| UnityMeshLab | `UnityMeshLab` (PascalCase) | ✅ pinned on profile, 1 star | C# | Mesh lab tooling (scope unverified) |
| UnityLodUvLightmapTransfer | unconfirmed | ❓ | — | — |
| lightmap-uv-tool | unconfirmed | ❓ | — | — |
| HLODSystem (fork of Unity-Technologies) | verified | ✅ | C# | LOD system fork |

The profile counter shows **9 public repos** total. Only 3 are pinned; the remaining 5 (plus HLODSystem) were not enumerable. **Working hypothesis**: `unitymeshlab` (lowercase in your original message) is the same repo as `UnityMeshLab`; `UnityLodUvLightmapTransfer` and `lightmap-uv-tool` may be earlier or rename-aliased names for the same package, or may be two of the five non-pinned repos. Phase 0 of the execution plan resolves this deterministically.

**Critical**: the GitHub URL path is case-insensitive for redirects, but `raw.githubusercontent.com` paths are **case-sensitive**. Use `PrefabDoctor` and `UnityMeshLab` (PascalCase) in all automation.

---

## Part 2 — Skill architecture redesign

### 2.1 Per-skill disposition

| Existing skill | Disposition | Rationale |
|---|---|---|
| `unity-editor-tooling` | **Rewrite** | Keep as umbrella for `EditorWindow`/`CustomEditor`/`[MenuItem]`; tighten description with trigger vocabulary; reference `_shared/` |
| `unity-assetdatabase-tools` | **Rewrite** | High-value skill, currently 1–3 lines. Expand with StartAssetEditing/StopAssetEditing, MoveAsset/RenameAsset, AssetPostprocessor |
| `unity-undo-prefab-safety` | **Rewrite + split references** | Becomes the flagship "editor mutation safety" skill. Reference `_checklists/undo-safety.md` and `_checklists/prefab-safety.md` |
| `unity-serialized-workflow` | **Rewrite** | Cover SerializedObject/Property/PropertyDrawer. Reference `_shared/version-gates.md` for 2021.3/2022.2/6 differences |
| `unity-package-architect` | **Rewrite** | Authoritative on UPM layout + package.json + asmdef. Reference `_template/package-template/` |
| `unity-package-reviewer` | **Keep + rewrite body** | Complementary to architect (review vs. author). Reference `_checklists/package-release.md` |
| `migration-and-refactor-planner` | **Rewrite + narrow scope** | Currently too generic. Scope to Unity-package migrations (namespace rename, asmdef restructure, Unity version bump) |
| `repo-auditor` | **Rewrite + narrow scope** | Scope to Unity UPM repo audits specifically; reference all checklists |
| **NEW** `unity-ci-validation` | **Create** | GameCI workflows, batch mode, `-runTests`, test result parsing, semver gate |
| **NEW** `unity-package-bootstrap` | **Create** | Entry point for `_template/package-template/`; creates a new package from template |
| **NEW** `repo-conventions` *(per repo)* | **Create** | Per-repo namespace, package name, CI specifics. Tiny file (≤ 80 lines). Resolves the global vs. local conventions tension |

### 2.2 Orchestration layer

Rules for cross-skill references and shared files, so `_shared/` and `_checklists/` stop being orphans:

1. **Every skill body ends with a "Further reading" section** that links to the two or three `_shared/` and `_checklists/` files it depends on. This is Anthropic's canonical one-level-deep pattern.
2. **`_shared/` files are loaded as Level-3 references** — short (≤ 150 lines), topical, canonical. Only SKILL.md files reference them; `_shared/` files never reference each other (avoids nested references Anthropic warns against).
3. **`_checklists/` files are action-ready** — imperative bullets, each with a verification command in backticks, intended for Claude to follow literally. They ARE nested references, but only from a SKILL.md, never from `_shared/`.
4. **`_template/package-template/` is accessed exclusively via `unity-package-bootstrap`** — which copies the template, performs variable substitution, and verifies the result.
5. **`repo-conventions` SKILL.md in each repo** overrides `_shared/naming-conventions.md` where they conflict and links to `_shared/naming-conventions.md` for everything else.

### 2.3 Resolving the namespace-conventions conflict

Two stances:
- **Stance A (strict):** enforce `SashaRX.<PackageName>` in `_shared/naming-conventions.md` and fix `lightmap-uv-tool`'s bare `LightmapUvTool` namespace via a migration.
- **Stance B (lenient):** relax `_shared/naming-conventions.md` to allow either pattern with a documented carve-out.

**Recommendation: Stance A.** Reasoning: bare single-segment namespaces are a known .NET anti-pattern — they collide with type names in IntelliSense, conflict with `using` aliases, and are explicitly discouraged by Microsoft's C# guidelines. Every surveyed Unity UPM package (UniTask, R3, NaughtyAttributes, MessagePipe, VContainer) uses `Author.Package` or `Company.Package`. Canonical rule to write:

> **Namespaces must follow `SashaRX.<PackageName>` (two segments minimum).** Example: `SashaRX.PrefabDoctor`, `SashaRX.UnityMeshLab`. Single-segment namespaces like `LightmapUvTool` are prohibited because they collide with C# type names and violate the reverse-DNS-analog convention used by every public Unity UPM package. The asmdef name, `rootNamespace`, and folder name under `Editor/`/`Runtime/` must all match. If a repository currently uses a bare namespace, apply migration via `migration-and-refactor-planner` before merging any new code.

The execution plan includes a migration step for any repo found to use a bare namespace.

---

## Part 3 — Per-skill rewrite specifications

Frontmatter conventions used below:
- Third person, single YAML line for `description`.
- Start description with imperative verb describing what the skill *does*, then `Use when …` clause with concrete trigger words (file extensions, API names, domain nouns).
- Target ~200–300 chars per description to leave room inside the 1,024 limit and the 1,536 Claude-Code combined cap.
- `paths` added where a file-pattern trigger makes sense (Claude Code extension).

### 3.1 `unity-editor-tooling`

```yaml
---
name: unity-editor-tooling
description: Write and review Unity Editor-only code — EditorWindow, CustomEditor, PropertyDrawer, menu items, IMGUI and UI Toolkit inspectors. Use when creating files under an Editor/ folder, writing [MenuItem], [CustomEditor], [CustomPropertyDrawer], [InitializeOnLoad], or any code inside an asmdef with includePlatforms Editor. Not for AssetDatabase batching or prefab mutation — delegate to unity-assetdatabase-tools and unity-undo-prefab-safety.
paths: ["**/Editor/**/*.cs"]
---
```

**Body outline** (~220 lines):
- Scope and what this skill is NOT for (delegations)
- Editor asmdef shape (canonical JSON block)
- EditorWindow lifecycle (OnEnable/OnDisable/OnGUI vs CreateGUI for UI Toolkit)
- CustomEditor skeleton (paired with SerializedObject workflow from sibling skill)
- PropertyDrawer vs DecoratorDrawer
- `[MenuItem]` path conventions, shortcut key conflicts, validation functions
- `[InitializeOnLoad]` / `[InitializeOnLoadMethod]` — when to use, cost implications
- Good/bad pattern pair: IMGUI minimum example vs. UI Toolkit minimum example
- Version gates: 2021.3 IMGUI-first, 2022.2+ UI Toolkit mature
- Further reading: `_shared/naming-conventions.md`, `_shared/version-gates.md`, `_shared/anti-patterns.md`

### 3.2 `unity-assetdatabase-tools`

```yaml
---
name: unity-assetdatabase-tools
description: Safely batch AssetDatabase operations, move and rename assets preserving GUIDs, and write AssetPostprocessors with recursion guards. Use when code touches AssetDatabase, AssetImporter, AssetPostprocessor, .meta files, or performs bulk asset creation, import, move, copy, or delete. Always wrap batches in try/finally with StartAssetEditing/StopAssetEditing (or AssetEditingScope on Unity 6+).
paths: ["**/*AssetPostprocessor*.cs", "**/*Importer*.cs"]
---
```

**Body outline** (~280 lines):
- The batching contract: `StartAssetEditing`/`StopAssetEditing` must be paired in `try/finally`; nest-counted. Reference [_checklists/batch-safety.md].
- Unity 6 `AssetEditingScope` (disposable) with version gate snippet
- Do NOT call any AssetDatabase query API between Start/Stop expecting fresh results — imports are deferred.
- `MoveAsset`/`RenameAsset`/`CopyAsset` semantics; never `File.Move`.
- `AssetPostprocessor`:
  - `GetPostprocessOrder`, `GetVersion` (increment on behavior change)
  - OnPreprocess* / OnPostprocess* / `OnPostprocessAllAssets` (+ `didDomainReload` on 2021.2+)
  - Recursion guard pattern with `HashSet<string>` + `try/finally`
  - `importer.userData` for round-trip state
  - **Ship postprocessors as DLLs in production** (avoid compile-error import lockup)
- Good/bad pair: batch import of 10,000 textures with/without `StartAssetEditing`
- Further reading: `_checklists/batch-safety.md`, `_shared/version-gates.md`, `_shared/anti-patterns.md`

### 3.3 `unity-undo-prefab-safety`

```yaml
---
name: unity-undo-prefab-safety
description: Make every editor mutation undoable and every prefab edit safe. Use when code modifies scene GameObjects, components, prefab assets, or prefab instance overrides, or when using Undo, PrefabUtility, PrefabStage, or EditorUtility.SetDirty. ALWAYS call Undo.RecordObject before mutation, PrefabUtility.RecordPrefabInstancePropertyModifications after instance edits, and EditPrefabContentsScope (2020.1+) for asset edits. Do not use File.* for assets — use AssetDatabase APIs (delegate to unity-assetdatabase-tools).
paths: ["**/Editor/**/*.cs"]
---
```

**Body outline** (~320 lines):
- Three mutation contexts: scene instance, prefab instance override, prefab asset
- Decision table: which API for which context
- `Undo.RecordObject` what it covers (serialized property delta) vs doesn't (parenting, AddComponent, destruction)
- `Undo.RegisterCompleteObjectUndo` / `RegisterFullObjectHierarchyUndo` / `RegisterCreatedObjectUndo` (order matters!)
- `Undo.AddComponent<T>` / `Undo.DestroyObjectImmediate` / `Undo.SetTransformParent`
- Grouping pattern: `IncrementCurrentGroup` → work → `SetCurrentGroupName` → `CollapseUndoOperations(GetCurrentGroup())`
- Prefab asset edit pattern: `PrefabUtility.EditPrefabContentsScope` (preferred) + manual pattern fallback
- Prefab instance override pattern: `RecordObject` then `RecordPrefabInstancePropertyModifications` — MUST be both, MUST be in that order
- `PrefabStage` API (namespace moved in 2021+; gate)
- Reference: [_checklists/undo-safety.md], [_checklists/prefab-safety.md]
- Further reading: `_shared/anti-patterns.md`, `_shared/version-gates.md`

### 3.4 `unity-serialized-workflow`

```yaml
---
name: unity-serialized-workflow
description: Implement CustomEditor and PropertyDrawer classes with correct SerializedObject lifecycle. Use when writing CustomEditor, CustomPropertyDrawer, EditorWindow with Inspector-style panels, or any code using SerializedObject/SerializedProperty/FindProperty/FindPropertyRelative. Always call serializedObject.Update() first and ApplyModifiedProperties() last; never call Update() mid-modification.
paths: ["**/Editor/**/*.cs"]
---
```

**Body outline** (~180 lines):
- Canonical `OnInspectorGUI` skeleton
- `FindProperty` vs `FindPropertyRelative`
- Multi-object editing: `new SerializedObject(targets)`
- `SerializedProperty` iteration (copy iterator; `NextVisible(true)`)
- `EditorGUI.BeginChangeCheck` + `EndChangeCheck` + `ApplyModifiedProperties` as the canonical change-detection triplet
- `OnValidate()` as the invariant-enforcement companion (flushed props bypass setters)
- `[SerializeField]` vs `[SerializeReference]` vs `ISerializationCallbackReceiver`
- When to use `ApplyModifiedPropertiesWithoutUndo`
- Shift-right-click trick for copying serialized paths (documentation aid)
- Further reading: `_shared/anti-patterns.md`, `_shared/version-gates.md`

### 3.5 `unity-package-architect`

```yaml
---
name: unity-package-architect
description: Author Unity UPM package skeletons with canonical layout — package.json, asmdef boundaries, Runtime/Editor/Tests split, Samples~ and Documentation~ tilde-hidden folders. Use when creating a new UPM package, restructuring a package, editing package.json, or adding/splitting asmdefs. Namespaces follow SashaRX.<PackageName>; see _shared/naming-conventions.md.
paths: ["**/package.json", "**/*.asmdef", "**/*.asmref"]
---
```

**Body outline** (~260 lines):
- Canonical folder tree (the one from §1.3)
- `package.json` field-by-field (with reverse-DNS name, SemVer, `unity` LTS target, `unityRelease`, `dependencies`, `samples` array)
- Asmdef pair pattern: `SashaRX.<Package>` (Runtime) + `SashaRX.<Package>.Editor` (Editor, `includePlatforms: ["Editor"]`) + `SashaRX.<Package>.Tests.Editor` + optional `SashaRX.<Package>.Tests.Runtime`
- `versionDefines` pattern for conditional compilation on other UPM packages / Unity versions
- `defineConstraints` vs `versionDefines` — when to use which
- `Samples~` convention and `samples` array correspondence
- `Documentation~` convention
- `Tests~` vs `Tests/` — ship-or-skip decision
- `.gitignore`/`.npmignore` rules for UPM
- Good/bad pair: flat Assets/ layout vs. tilde-hidden conventional layout
- Further reading: `_shared/naming-conventions.md`, `_shared/version-gates.md`, `_template/package-template/`

### 3.6 `unity-package-reviewer`

```yaml
---
name: unity-package-reviewer
description: Audit a Unity UPM package for release readiness — verify package.json, asmdef platform filters, CHANGELOG conformance, SemVer bump correctness, Samples~ wiring, and absence of Assets/-only references. Use when preparing a release, merging a version bump PR, reviewing a UPM package for publication, or inspecting package.json changes. Runs through _checklists/package-release.md end to end.
paths: ["**/package.json", "**/CHANGELOG.md"]
---
```

**Body outline** (~200 lines):
- Review phases mapped to `_checklists/package-release.md`
- SemVer decision tree (patch/minor/major) with Unity-package-specific signals (asmdef reference added = minor; public API removed = major)
- CHANGELOG.md Keep-a-Changelog format check
- `samples` array vs `Samples~/` folder cross-check
- `docs~` presence + `documentationUrl` validity
- Asmdef platform-filter audit (no Editor asmdef leaking into Runtime deps)
- `dependencies` version range sanity
- Further reading: `_checklists/package-release.md`, `_shared/naming-conventions.md`

### 3.7 `migration-and-refactor-planner`

```yaml
---
name: migration-and-refactor-planner
description: Plan a safe migration inside a Unity UPM package — namespace rename, asmdef restructure, Unity minimum-version bump, or API deprecation. Use when renaming a namespace across many files, splitting or merging asmdefs, bumping the "unity" field in package.json, or deprecating public API that downstream samples or tests depend on. Always sequence: snapshot → migrate → regenerate GUIDs only if unavoidable → run tests → bump SemVer.
---
```

**Body outline** (~200 lines):
- Scope: intra-package migrations, not cross-repo.
- Phase template: snapshot (git tag), discovery (grep/asmdef scan), plan, execute, validate, commit, bump.
- Namespace rename: use `AssetDatabase.RenameAsset` for asmdef files; use `sed`/rename tool for C# `namespace` blocks; match asmdef `rootNamespace`.
- Asmdef split: preserve GUIDs by keeping the original asmdef file and creating new ones; never delete-and-recreate an asmdef with the same name.
- Unity minimum bump: update `package.json` `unity`; scan for APIs that existed in the old version but not the new; run matrix CI.
- API deprecation: `[Obsolete]` with `false` (warning) first, then `true` (error) next minor, then removal next major.
- Further reading: `_checklists/package-release.md`, `_shared/version-gates.md`, `_shared/naming-conventions.md`

### 3.8 `repo-auditor`

```yaml
---
name: repo-auditor
description: Audit a Unity UPM repository's health — skill directory structure, AGENTS.md/CLAUDE.md coherence, CI workflow presence, LICENSE, CHANGELOG, package.json correctness, .gitignore safety. Use when onboarding a new repo, before a release, or when asked to "audit", "review the repo", or "check project health". Produces a prioritized findings report with line-referenced citations.
---
```

**Body outline** (~180 lines):
- Audit dimensions (table): skills, agent docs, CI, license, changelog, package.json, asmdef, gitignore, samples, docs.
- Output format: Markdown report with severity (critical/warning/info) + file:line citations + suggested fix command.
- Calls out to `_checklists/package-release.md` for the release subset.
- Integration: can hand off findings as inputs to `migration-and-refactor-planner`.
- Further reading: all four `_checklists/*.md`, `_shared/anti-patterns.md`

### 3.9 NEW — `unity-ci-validation`

```yaml
---
name: unity-ci-validation
description: Author and debug Unity CI workflows for UPM packages — GameCI actions, batch mode EditMode and PlayMode test runs, license activation, NUnit result parsing, and release gates on SemVer. Use when creating or editing .github/workflows/*.yml for Unity, diagnosing a failed CI run, or wiring semantic-release / GitHub Release automation. Knows -batchmode -nographics -runTests -testPlatform -testResults conventions and Application.isBatchMode guards.
paths: [".github/workflows/**/*.yml", ".github/workflows/**/*.yaml"]
---
```

**Body outline** (~240 lines):
- GameCI skeleton workflow (test + release job) with pinned action versions.
- License activation variants: personal (manual activation file) vs professional (serial secret).
- EditMode vs PlayMode matrix; `unity-versions` matrix across LTS.
- Result parsing: NUnit XML to GitHub Checks annotations.
- `-executeMethod` for custom build/export entrypoints; always guard with `Application.isBatchMode` to avoid blocking dialogs.
- Release automation via semantic-release with Unity-specific plugin config, or manual `npm version` + tag push.
- Secret management and minimum scopes.
- Further reading: `_checklists/package-release.md`, `_shared/anti-patterns.md`

### 3.10 NEW — `unity-package-bootstrap`

```yaml
---
name: unity-package-bootstrap
description: Bootstrap a new Unity UPM package from the bundled _template/package-template/ — creates folder structure, substitutes {{PackageName}}, {{Namespace}}, {{DisplayName}}, {{UnityMinVersion}} placeholders, renames .template files, initializes git, and verifies the package imports cleanly. Use when the user asks to "create a new package", "scaffold a UPM package", "bootstrap a new Unity tool", or any request to start a new Unity editor extension.
---
```

**Body outline** (~160 lines):
- Template inventory: what's in `_template/package-template/`
- Parameters: `{{PackageName}}` (PascalCase), `{{Namespace}}` (= `SashaRX.{{PackageName}}`), `{{PackageId}}` (= `com.sasharx.<lowercase-package>`), `{{DisplayName}}`, `{{Description}}`, `{{UnityMinVersion}}` (default `2021.3`), `{{Author}}` (default `SashaRX`), `{{License}}` (default `MIT`)
- Execution sequence: copy template → substitute → rename `.template` suffix → `git init` → `git add` → initial commit
- Post-bootstrap verification: package.json JSON-parseable, asmdef JSON-parseable, no `{{...}}` markers remain, file tree matches canonical layout
- Integration with `unity-ci-validation` to optionally add workflows
- Further reading: `unity-package-architect`, `_shared/naming-conventions.md`

### 3.11 NEW — per-repo `repo-conventions`

One SKILL.md per repo, intentionally small.

```yaml
---
name: repo-conventions
description: Canonical conventions for THIS repository — the package ID, namespace, Unity version target, CI workflow names, and any deviations from _shared/naming-conventions.md. Use at the start of any non-trivial task in this repo, when creating new C# files (to pick the correct namespace), when adding asmdefs, or when editing package.json. Overrides _shared/* where they conflict.
---
```

**Body outline** (~60 lines, per-repo variables filled):

```
# repo-conventions (PrefabDoctor)

## Identity
- Package ID: com.sasharx.prefabdoctor
- Display Name: Prefab Doctor
- Root Namespace: SashaRX.PrefabDoctor
- Unity Min Version: 2021.3

## Assemblies
- Runtime:        SashaRX.PrefabDoctor (rootNamespace = SashaRX.PrefabDoctor)
- Editor:         SashaRX.PrefabDoctor.Editor       (includePlatforms: ["Editor"])
- Tests (Editor): SashaRX.PrefabDoctor.Tests.Editor (includePlatforms: ["Editor"])

## CI Workflows
- .github/workflows/test.yml    — EditMode/PlayMode on 2021.3, 2022.3, Unity 6
- .github/workflows/release.yml — Semantic release on tag push

## Deviations from _shared/
- None.

## Further reading
- _shared/naming-conventions.md
- _shared/version-gates.md
```

(Equivalent files for `UnityMeshLab` and for the lightmap-uv repo with its own variable values, filled in Phase 0.)

---

## Part 4 — Execution plan for Claude Code

This is a **paste-ready prompt** for Claude Code. Run it in each of the three repositories. Per-repo variables are resolved in Phase 0 by discovery, so the same prompt body works everywhere.

### Parameters resolved by Phase 0

| Variable | Discovered from | Example (PrefabDoctor) |
|---|---|---|
| `$REPO_NAME` | `git remote get-url origin` basename, PascalCase | `PrefabDoctor` |
| `$PACKAGE_ID` | `package.json` `.name` | `com.sasharx.prefabdoctor` |
| `$DISPLAY_NAME` | `package.json` `.displayName` or inferred | `Prefab Doctor` |
| `$NAMESPACE` | scan `Editor/**/*.cs` + `Runtime/**/*.cs` first `namespace` token | `SashaRX.PrefabDoctor` |
| `$UNITY_MIN` | `package.json` `.unity` | `2021.3` |
| `$HAS_AGENTS_MD` | `test -f AGENTS.md` | `true`/`false` |
| `$HAS_CLAUDE_MD` | `test -f CLAUDE.md` | `true`/`false` |
| `$CI_WORKFLOWS` | `ls .github/workflows` | `test.yml release.yml` |
| `$DEFAULT_BRANCH` | `git symbolic-ref refs/remotes/origin/HEAD` | `main` |

### Phase 0 — Pre-flight (≈ 3 minutes)

**Goal:** fingerprint the repo, snapshot state, detect conflicts.

```bash
# 0.1 Verify we're in a Unity UPM repo
test -f package.json || { echo "FAIL: no package.json at repo root"; exit 1; }

# 0.2 Snapshot
git status --porcelain | tee /tmp/pre-overhaul-dirty.txt
git rev-parse HEAD | tee /tmp/pre-overhaul-sha.txt
git tag -a "pre-skills-overhaul-$(date +%Y%m%d)" -m "Snapshot before skills overhaul"

# 0.3 Resolve parameters
REPO_NAME=$(basename -s .git "$(git remote get-url origin)")
PACKAGE_ID=$(jq -r '.name' package.json)
DISPLAY_NAME=$(jq -r '.displayName // empty' package.json)
UNITY_MIN=$(jq -r '.unity' package.json)
DEFAULT_BRANCH=$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's@^origin/@@' || echo main)

# 0.4 Detect actual namespace in C# source
NAMESPACE=$(grep -hEr '^namespace ' --include='*.cs' Editor Runtime 2>/dev/null \
  | head -1 | awk '{print $2}' | tr -d '{' | xargs)
echo "Detected namespace: $NAMESPACE"

# 0.5 Detect existing skills and agent files
ls -la .claude/skills 2>/dev/null
test -f AGENTS.md && echo "AGENTS.md present"
test -f CLAUDE.md && echo "CLAUDE.md present"
ls .github/workflows 2>/dev/null

# 0.6 Detect namespace convention conflict
if [ -n "$NAMESPACE" ] && ! echo "$NAMESPACE" | grep -qE '^SashaRX\.'; then
  echo "WARN: namespace '$NAMESPACE' does not match SashaRX.<Package> — migration required in Phase 2."
fi

# 0.7 Write the parameter file used by later phases
cat > /tmp/skills-overhaul.env <<EOF
REPO_NAME=$REPO_NAME
PACKAGE_ID=$PACKAGE_ID
DISPLAY_NAME=$DISPLAY_NAME
NAMESPACE=$NAMESPACE
UNITY_MIN=$UNITY_MIN
DEFAULT_BRANCH=$DEFAULT_BRANCH
EOF
cat /tmp/skills-overhaul.env
```

**Verification:** `/tmp/skills-overhaul.env` is non-empty; working tree either clean or known-dirty; git tag created.

**Rollback:** `git reset --hard <pre-overhaul-sha>` (SHA saved in `/tmp/pre-overhaul-sha.txt`), and `git tag -d pre-skills-overhaul-<date>`.

### Phase 1 — Structural changes

**Goal:** Create missing directories, remove empty/broken skill stubs, prepare English scaffolding.

```bash
cd .claude/skills

# 1.1 Ensure standard directories exist (idempotent)
mkdir -p _checklists _shared _template/package-template/Editor _template/package-template/Tests/Editor

# 1.2 For each existing skill, create references/ and scripts/ subfolders (even if unused initially)
for skill in unity-editor-tooling unity-assetdatabase-tools unity-undo-prefab-safety \
             unity-serialized-workflow unity-package-architect unity-package-reviewer \
             migration-and-refactor-planner repo-auditor; do
  [ -d "$skill" ] && mkdir -p "$skill/references" "$skill/scripts" 2>/dev/null || true
done

# 1.3 Create directories for new skills
mkdir -p unity-ci-validation/references unity-ci-validation/scripts
mkdir -p unity-package-bootstrap/references unity-package-bootstrap/scripts
mkdir -p repo-conventions

# 1.4 Archive any existing non-English skill bodies for reference during rewrite
mkdir -p _archive
for skill in */SKILL.md; do
  [ -f "$skill" ] && cp "$skill" "_archive/$(dirname "$skill").pre-overhaul.md"
done

cd ../..
git add .claude/skills
git status
```

**Commit:**
```bash
git commit -m "chore(skills): scaffold directory structure for overhaul

- Add references/ and scripts/ subfolders under each skill
- Create directories for unity-ci-validation, unity-package-bootstrap, repo-conventions
- Archive pre-overhaul SKILL.md content under .claude/skills/_archive/"
```

**Verification:**
```bash
find .claude/skills -type d | sort
test -d .claude/skills/_archive
```

**Rollback:** `git reset --hard HEAD~1` (this commit only).

### Phase 2 — Content rewrites

**Goal:** Write English SKILL.md bodies per Part 3 specs. Populate `_shared/` and `_checklists/`. This is the largest phase; break into sub-commits.

Instruction to Claude Code (in-session prompt after Phase 1 commit):

> For each SKILL.md file listed in Part 3 of the plan, open the corresponding archive at `.claude/skills/_archive/<skill>.pre-overhaul.md` to understand the author's original intent, then overwrite `.claude/skills/<skill>/SKILL.md` with:
> 1. Exactly the YAML frontmatter from Part 3.
> 2. A body that matches the outline in Part 3, under 500 lines, with fenced code blocks for good/bad pattern pairs, using forward-slash paths, in imperative third-person English, and ending with a **Further reading** section linking only to files in `_shared/` and `_checklists/` one level deep.
> 3. No XML tags, no `{{placeholder}}` markers left unresolved.
> After each SKILL.md, run `wc -l` to confirm under 500 lines and print the frontmatter.

**2.1 Write `_shared/` files** (short, canonical, ≤ 150 lines each):

**`_shared/naming-conventions.md`** (~90 lines):
- Reverse-DNS package ID (`com.sasharx.<package>`), lowercase
- Two-segment namespace rule: `SashaRX.<Package>` (justified in one paragraph; bare single-segment namespaces prohibited)
- Asmdef naming: `SashaRX.<Package>`, `SashaRX.<Package>.Editor`, `SashaRX.<Package>.Tests.Editor`
- `rootNamespace` field of asmdef must match the `namespace` block of every `.cs` inside
- Folder naming: PascalCase for `.cs`-holding folders; tilde suffix for `Samples~`/`Documentation~`/`Tests~`
- File naming: `ClassName.cs` (one public class per file), `ClassName.PartName.cs` for partials
- Explicit deviation protocol: the per-repo `repo-conventions` SKILL.md documents any carve-out

**`_shared/version-gates.md`** (~110 lines):
- Table of `UNITY_X_Y_OR_NEWER` vs what it enables (key rows: 2021.3, 2022.2, 2023.1, 6000.0)
- Gotchas: no patch-level `_OR_NEWER`; use asmdef `versionDefines` with `"name": "Unity"` for patch-level
- Asmdef `versionDefines` expression syntax (interval notation)
- Specific gate recipes used in this repo's skills:
  - `EditPrefabContentsScope` — available since 2020.1
  - `PrefabStage` namespace moved in 2021.2
  - `AssetEditingScope` disposable — Unity 6000.0+
  - `OnPostprocessAllAssets` `didDomainReload` overload — 2021.2+
- Policy: target `$UNITY_MIN` per `package.json`; anything newer needs explicit gating

**`_shared/anti-patterns.md`** (~120 lines):
Sections: Undo pitfalls (RecordObject on parenting, missing RecordPrefabInstancePropertyModifications), AssetDatabase pitfalls (File.Move, unreleased StartAssetEditing, query between Start/Stop), Serialization pitfalls (Update() mid-mutation, first-person field names, leaking MeshFilter.mesh in edit mode), Packaging pitfalls (missing tilde on Samples, asmdef with both include+exclude, Unity minor bump without CI matrix update), Skill-authoring pitfalls (echo the cross-cutting ones from §1.1). Each item: one line symptom + one line root cause + one line fix.

**2.2 Write `_checklists/` files** (imperative, with verification commands):

**`_checklists/batch-safety.md`** (~60 lines): numbered pre-/mid-/post-batch checks.
**`_checklists/undo-safety.md`** (~60 lines): mutation-context decision tree + RecordObject-before-mutation enforcement.
**`_checklists/prefab-safety.md`** (~70 lines): asset vs override vs scene-instance paths.
**`_checklists/package-release.md`** (~100 lines): SemVer decision, CHANGELOG entry, asmdef audit, Samples array cross-check, CI green, tag-push procedure.

**2.3 Write each SKILL.md body per Part 3.** Aim for ~200 lines per skill on average; none above 400. Verify individually.

**Commit strategy for Phase 2 (one commit per logical group):**
```bash
git add .claude/skills/_shared
git commit -m "docs(skills): add canonical _shared/ references (naming, version gates, anti-patterns)"

git add .claude/skills/_checklists
git commit -m "docs(skills): flesh out _checklists/ with imperative action items"

git add .claude/skills/unity-editor-tooling .claude/skills/unity-assetdatabase-tools \
        .claude/skills/unity-undo-prefab-safety .claude/skills/unity-serialized-workflow
git commit -m "docs(skills): rewrite editor + assetdb + undo + serialization skills in English"

git add .claude/skills/unity-package-architect .claude/skills/unity-package-reviewer
git commit -m "docs(skills): rewrite UPM architect + reviewer skills in English"

git add .claude/skills/migration-and-refactor-planner .claude/skills/repo-auditor
git commit -m "docs(skills): rewrite migration-planner + repo-auditor with narrower scope"
```

**Per-SKILL.md verification:**
```bash
for skill in .claude/skills/*/SKILL.md; do
  lines=$(wc -l < "$skill")
  desc=$(awk '/^description:/{sub(/^description: */,""); print; exit}' "$skill")
  desclen=${#desc}
  echo "$skill  lines=$lines  desclen=$desclen"
  [ "$lines" -le 500 ] || echo "  WARN over 500 lines"
  [ "$desclen" -le 1024 ] || echo "  FAIL description over 1024 chars"
  [ "$desclen" -ge 80 ] || echo "  WARN description under 80 chars — likely too terse"
done
```

**Rollback:** each sub-commit can be reverted independently with `git revert <sha>`.

### Phase 3 — New skills creation

**Goal:** Create the three net-new skills from §3.9–§3.11.

```bash
# 3.1 unity-ci-validation — reference workflow template in references/
mkdir -p .claude/skills/unity-ci-validation/references
# (Claude Code writes SKILL.md per §3.9 + references/gameci-workflow.yml example)

# 3.2 unity-package-bootstrap — script does template substitution
mkdir -p .claude/skills/unity-package-bootstrap/scripts
# (Claude Code writes SKILL.md per §3.10 + scripts/bootstrap.sh)

# 3.3 repo-conventions — single SKILL.md with per-repo values filled from /tmp/skills-overhaul.env
source /tmp/skills-overhaul.env
# (Claude Code writes .claude/skills/repo-conventions/SKILL.md with $REPO_NAME, $PACKAGE_ID,
#  $NAMESPACE, $UNITY_MIN substituted)
```

**Verification:**
```bash
grep -r '{{' .claude/skills/repo-conventions/ && echo "FAIL: unresolved placeholders"
jq -e . .claude/skills/unity-ci-validation/references/*.yml >/dev/null 2>&1 # if YAML is also JSON-parseable where applicable
```

**Commit:**
```bash
git add .claude/skills/unity-ci-validation .claude/skills/unity-package-bootstrap .claude/skills/repo-conventions
git commit -m "feat(skills): add unity-ci-validation, unity-package-bootstrap, and per-repo repo-conventions"
```

### Phase 3.5 — Namespace migration (conditional)

Run **only** if Phase 0 step 0.6 logged the `WARN: namespace ... does not match` message (i.e., the repo currently uses a bare namespace like `LightmapUvTool`).

```bash
source /tmp/skills-overhaul.env
OLD_NS="$NAMESPACE"
NEW_NS="SashaRX.${REPO_NAME}"

# 3.5.1 Create snapshot branch
git checkout -b "chore/namespace-migration-$(date +%Y%m%d)"

# 3.5.2 Replace in .cs files (word-boundary safe)
grep -rl --include='*.cs' -w "$OLD_NS" Editor Runtime Tests 2>/dev/null | while read f; do
  sed -i.bak "s/\b$(printf '%s' "$OLD_NS" | sed 's/\./\\./g')\b/$NEW_NS/g" "$f" && rm "${f}.bak"
done

# 3.5.3 Update asmdef rootNamespace + name fields
for asmdef in $(git ls-files '*.asmdef'); do
  jq --arg old "$OLD_NS" --arg new "$NEW_NS" \
     'if .rootNamespace == $old then .rootNamespace = $new else . end
      | if (.name | startswith($old + ".") or . == $old)
          then .name = ($new + (.name|ltrimstr($old))) else . end' \
     "$asmdef" > "$asmdef.tmp" && mv "$asmdef.tmp" "$asmdef"
done

# 3.5.4 Rebuild in editor (Claude Code instructs user to reopen Unity or runs batch-mode compile check)
# 3.5.5 Run tests
# 3.5.6 Commit
git add .
git commit -m "refactor: migrate namespace $OLD_NS -> $NEW_NS

Align with _shared/naming-conventions.md two-segment rule (SashaRX.<Package>).
Asmdef rootNamespace and name fields updated."
```

**Rollback:** `git checkout $DEFAULT_BRANCH && git branch -D chore/namespace-migration-*`.

### Phase 4 — Validation

Run a battery of checks and abort if any fail.

```bash
# 4.1 Frontmatter validation — required keys, description length, name pattern
for skill in .claude/skills/*/SKILL.md; do
  python3 - <<PY
import sys, yaml, re, pathlib
p = pathlib.Path("$skill")
text = p.read_text()
assert text.startswith("---\n"), f"{p}: no frontmatter"
_, fm, _ = text.split("---\n", 2)
meta = yaml.safe_load(fm)
for k in ("name", "description"):
  assert k in meta, f"{p}: missing {k}"
assert re.fullmatch(r"[a-z0-9-]{1,64}", meta["name"]), f"{p}: invalid name '{meta['name']}'"
assert 1 <= len(meta["description"]) <= 1024, f"{p}: description length {len(meta['description'])}"
assert not re.search(r"\b(I|you|we)\b", meta["description"]), f"{p}: first/second-person in description"
allowed = {"name","description","license","allowed-tools","metadata","when_to_use","paths",
           "argument-hint","disable-model-invocation","user-invocable","model","effort",
           "context","agent","hooks","shell"}
extra = set(meta) - allowed
assert not extra, f"{p}: forbidden frontmatter keys {extra}"
print(f"OK {p}")
PY
done

# 4.2 Body length
find .claude/skills -name SKILL.md -exec sh -c '
  lines=$(wc -l < "$1"); [ "$lines" -le 500 ] || echo "WARN $1 has $lines lines"
' _ {} \;

# 4.3 Cross-reference integrity: every link in SKILL.md must resolve
for skill in .claude/skills/*/SKILL.md; do
  dir=$(dirname "$skill")
  grep -oE '\]\(([^)]+\.md)\)' "$skill" | sed -E 's/\]\((.+)\)/\1/' | while read ref; do
    case "$ref" in
      /*) path="$ref" ;;
      *)  path="$dir/$ref" ;;
    esac
    # Also support _shared/_checklists one-level-up references
    if [ ! -f "$path" ] && [ -f ".claude/skills/$ref" ]; then
      path=".claude/skills/$ref"
    fi
    [ -f "$path" ] || echo "BROKEN $skill -> $ref"
  done
done

# 4.4 No Windows backslashes in any skill file
grep -rnP '[^\\]\\[A-Za-z]' .claude/skills --include='*.md' && echo "WARN backslashes found"

# 4.5 No leftover non-English or placeholder markers
grep -rnP '[А-Яа-яЁё]' .claude/skills --include='*.md' && echo "FAIL non-English text"
grep -rn '{{' .claude/skills --include='*.md' && echo "FAIL unresolved placeholders"

# 4.6 Claude Code budget simulation — concat name+description per skill and ensure each is under 1536
for skill in .claude/skills/*/SKILL.md; do
  name=$(awk '/^name:/{print $2; exit}' "$skill")
  desc=$(awk '/^description:/{sub(/^description: */,""); print; exit}' "$skill")
  total=$(( ${#name} + ${#desc} ))
  [ "$total" -le 1536 ] || echo "WARN $skill listing $total over 1536"
done

# 4.7 Launch Claude Code and verify skills appear
echo "Manual step: run 'claude' and ask 'list available skills' — confirm all 11 skills listed."
```

**Commit (no content changes, only potential fixes from 4.x):**
```bash
git diff --exit-code .claude/skills && echo "Phase 4 clean"
```

### Phase 5 — Commit strategy and final push

Phases 1–3.5 each already produced a commit. The final phase pushes and tags.

```bash
git log --oneline "pre-skills-overhaul-$(date +%Y%m%d)"..HEAD
git push origin HEAD:refs/heads/chore/skills-overhaul
# Open PR; title: "chore(skills): overhaul .claude/skills/ to English + progressive disclosure"
# Body: link to this plan, list of commits, before/after line counts
```

After merge:
```bash
git tag -a skills-overhaul-complete -m "Skills overhaul complete for $REPO_NAME"
git push origin skills-overhaul-complete
```

### Per-repo customization section

Fill the following table once Phase 0 runs in each repo. The plan body does not change; only these values do.

| Variable | PrefabDoctor | UnityMeshLab | lightmap-uv-tool (if separate) |
|---|---|---|---|
| `REPO_NAME` | `PrefabDoctor` | `UnityMeshLab` | *(resolve in Phase 0)* |
| `PACKAGE_ID` | `com.sasharx.prefabdoctor` | `com.sasharx.unitymeshlab` | `com.sasharx.lightmap-uv-tool` |
| `DISPLAY_NAME` | Prefab Doctor | Unity Mesh Lab | Lightmap UV Tool |
| `NAMESPACE` | `SashaRX.PrefabDoctor` | `SashaRX.UnityMeshLab` | target `SashaRX.LightmapUvTool` (migrate if currently bare) |
| `UNITY_MIN` | *(from package.json)* | *(from package.json)* | *(from package.json)* |
| Primary domain | prefab override/variant analysis, prefab health scan | mesh lab tooling | lightmap UV transfer, LOD UV workflow |
| CI workflows | *(Phase 0 discovery)* | *(Phase 0 discovery)* | *(Phase 0 discovery)* |
| Bare-namespace migration needed | likely no | likely no | **yes, if AGENTS.md claim holds** |
| Primary domain vocabulary for `repo-conventions` description triggers | `nested prefab, override conflict, prefab health, prefab variant` | `mesh editor, LOD group, mesh combine` | `lightmap UV, LOD UV, UV transfer, uv2, baked lightmap` |

---

## Part 5 — Risk register and open questions

### 5.1 Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| AGENTS.md or CLAUDE.md contradicts new SKILL.md bodies | Medium | High — guidance drift confuses Claude | Phase 0 detects presence; Phase 2 sub-task reconciles and points AGENTS.md to the skills directory instead of duplicating rules |
| Private repo among the three — plan can't be validated externally | Possible | Medium | Plan is self-contained and runs locally; no external fetches required |
| SashaRX.github.io or HLODSystem accidentally receives the overhaul | Low | Medium | Phase 0's `test -f package.json` gate rejects non-UPM repos |
| Skill description drift from 1024-char limit after later edits | Medium | Low | Phase 4's validator is a copyable script; wire into a `pre-commit` hook |
| Prettier reflows YAML descriptions into multi-line folded scalars | Medium | High — skill invisible | Add `.prettierignore` entry `.claude/skills/**/SKILL.md` in Phase 1 |
| `SLASH_COMMAND_TOOL_CHAR_BUDGET` overflow hides skills | Medium | High | Phase 4 logs estimated total listing chars; recommend `export SLASH_COMMAND_TOOL_CHAR_BUDGET=30000` in user shell |
| Namespace migration breaks external callers of `lightmap-uv-tool` | Low | High for downstream users | Tag a `pre-namespace-migration-vX.Y.Z` release before the migration; publish migration note in CHANGELOG |
| `EditPrefabContentsScope` unavailable on some Unity version the repo still supports | Depends on `$UNITY_MIN` | Medium | `_shared/version-gates.md` documents the 2020.1 threshold; skills use `#if UNITY_2020_1_OR_NEWER` guards |
| Claude Code session started before the directory existed | Medium | Low | Anthropic docs: creating a top-level skills dir mid-session requires restart; document in a README stub |
| Skill bodies drift to >500 lines over time | Medium | Medium | Phase 4 validator caps; extract to `references/` as content grows |
| `repo-conventions` becomes stale vs `package.json` | High | Medium | Add a tiny CI step: `jq '.name, .unity' package.json` must match the values in `repo-conventions/SKILL.md` |

### 5.2 Open questions for Sasha before Phase 3

1. **Confirm the three repos.** Are `UnityLodUvLightmapTransfer` and `lightmap-uv-tool` the same repo, renames of `UnityMeshLab`, or distinct? Run Phase 0 in what you believe to be the three repos and share the `/tmp/skills-overhaul.env` file from each.
2. **Namespace policy.** Do you accept Stance A (strict `SashaRX.<Package>`, migrate bare namespaces)? If you prefer Stance B (allow bare), `_shared/naming-conventions.md` content changes in Phase 2.
3. **Unity version target per repo.** Phase 0 reads `package.json` `unity`; confirm you want to keep that or bump to `2022.3` as part of this overhaul.
4. **CI expectations.** Should `unity-ci-validation` ship a concrete starter workflow into `.github/workflows/`, or only author guidance for when the user adds one manually?
5. **License uniformity.** Is MIT consistent across all three? The `_template/package-template/LICENSE` file needs a canonical copy.
6. **Description voice.** Default is third-person "Use when …". For the three most-critical skills (`unity-undo-prefab-safety`, `unity-assetdatabase-tools`, `unity-package-architect`), do you want directive "ALWAYS invoke …" phrasing per Seleznov's experiment? Marginally higher trigger rate at a directive-saturation risk.
7. **English-only reaffirmation.** All archived Russian content stays in `_archive/` and is not referenced from any active skill. Confirm that's fine.
8. **AGENTS.md relationship.** Keep AGENTS.md, shrink it to a pointer to `.claude/skills/`, or delete it?

### 5.3 Success criteria

The overhaul is successful when, in each of the three repos:

1. `find .claude/skills -name SKILL.md | wc -l` returns **11** (8 existing rewritten + 3 new).
2. Phase 4 validator exits clean — no FAIL lines, warnings understood.
3. Opening the repo in Claude Code and asking "list available skills" returns all 11 with their English descriptions visible in full (no truncation from listing-budget overflow).
4. Asking Claude Code "how should I batch 10,000 texture reimports safely in this repo?" triggers `unity-assetdatabase-tools` without prompting.
5. Asking "rename the namespace" triggers `migration-and-refactor-planner` and references `_shared/naming-conventions.md`.
6. Creating a new test file under `Tests/Editor/` surfaces `repo-conventions` and `unity-serialized-workflow` guidance automatically (via `paths` matching).
7. `_shared/` and `_checklists/` are each referenced by at least three different SKILL.md files (progressive disclosure demonstrably wired).
8. Every SKILL.md body is ≤ 500 lines; every description is ≤ 1024 chars and in third person.
9. The namespace conflict is resolved — all `.cs` files under `Editor/` and `Runtime/` use `SashaRX.<Package>` (or the explicit deviation documented in `repo-conventions`).
10. A fresh clone + `claude` invocation with `SLASH_COMMAND_TOOL_CHAR_BUDGET` at default 15,000 still lists all 11 skills in full.

---

## TL;DR — first three actions to take

1. **Run Phase 0 in each of the three repos and paste back the `/tmp/skills-overhaul.env` output for each.** This resolves the repo-identity question (are `UnityLodUvLightmapTransfer` / `lightmap-uv-tool` the same as `UnityMeshLab`?) and gives the plan the parameter values it needs. Estimated time: 3 minutes per repo.
2. **Decide the two policy questions in §5.2**: (a) namespace Stance A vs B and (b) whether to include directive "ALWAYS invoke" phrasing on the three most-critical skills. These decisions change only a handful of lines in `_shared/naming-conventions.md` and in three frontmatter descriptions.
3. **Execute Phase 1 (scaffold) in one repo as a pilot**, then Phase 2.1 (`_shared/` and `_checklists/` files) — these are repo-agnostic and can be copied identically to the other two repos once validated. Commit per the sub-commit plan. Only after that pilot runs cleanly through Phase 4 validation do you replicate to the remaining repos.