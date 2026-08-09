# Vertex Color Baking — Architecture & Roadmap

**Status:** design phase, scaffold not yet started
**Branch of record:** `claude/vertex-color-baking-sfTU3`
**Last update:** 2026-04-26
**Owner:** Vertex Color Baking tab (`Editor/Tools/VertexColorBakingTool.cs`)

This document captures the long-term architecture for the Vertex Color
Baking tab. The first deliverable (Phases 1–5 below) renamed the AO tab
and added a single Solid Color batch export flow. The rest of this doc
describes the layered system the tab will grow into so future stages
(noise, gradients, tint, blur, submesh, AO blends, etc.) compose
predictably instead of accumulating as ad-hoc `if/else` branches.

---

## 1. Goals

1. **Multiple bake modes that compose.** Solid, Gradient (axis-aligned),
   Noise (random / Perlin / Voronoi, monochrome or colored), Tint,
   Blur, Submesh tints, AO. Output is `mesh.colors32` (RGBA per vertex).
2. **Layer stack semantics** like Photoshop / Substance: ordered list
   of layers, each with a stage type, blend op, opacity, and enable
   toggle. Layers composite bottom-to-top into a single final color.
3. **Variant batch export** (already wired): one source FBX/prefab,
   N output variants, each variant = its own layer stack + suffix.
4. **AO migrates into the same system** — `AOStage` wraps the existing
   `VertexAOBaker` so AO can blend with Solid / Tint / Noise like any
   other stage. Migration is the last roadmap step so the rich AO UI
   keeps working until the layered system is stable.
5. **Persistence** — stacks survive Unity restarts and travel with the
   prefab/FBX through git. Sidecar asset preferred over `EditorPrefs`.

## 2. Non-goals (current scope)

- Interactive vertex painting brush.
- Procedural curve UI for gradients (use `UnityEngine.Gradient` until a
  user actually asks for more).
- Importing vertex colors from external textures or other meshes.
- Real-time preview shader inside the MeshLab viewport (Console-driven
  feedback is enough until the layered system stabilizes).

## 3. Current state — what already shipped

The tab was renamed and a working Solid Color path exists end-to-end.
Future phases refactor this into the layered model below; nothing here
gets thrown away, but the entry points move.

| Phase | Commit  | Outcome |
|-------|---------|---------|
| 1     | `0a2cafc` | `VertexAOTool` → `VertexColorBakingTool`. Asset GUID preserved. |
| 2     | `ed3df9b` | `BakeKind` toolbar (`AO` / `Solid Color`) + Solid bake path that writes `mesh.colors32` with collision skip and `Undo.RecordObject`. |
| 3a    | `e526307` | `LightmapTransferTool.ExportVertexColorsToFbxCore` accepts `outputFbxPathOverride`, returns `bool`. New public `ExportVertexColorsToFbxAs`. |
| 3b    | `8d65ef2` | `VariantExportPipeline.cs` — bake → FBX export → prefab clone (full clone, not Prefab Variant). Suffix validation, `ConflictPolicy`, batch wrapped in `StartAssetEditing/StopAssetEditing`. |
| 4     | `badc0ec` | UI: `(Color, suffix)` variant list with `[+]/[−]`, preview line, `[Bake (preview)]` and `[Bake & Export All]` buttons. |
| 5     | `e88ff18` | README section + `[Unreleased]` CHANGELOG entry. |

Known limitations these phases inherit from the "single solid color
per variant" design:

- One `Color` per variant, not a stack.
- AO and Solid live in two disjoint UI branches with no shared compute
  primitive.
- `VariantExportPipeline.BakeSolidColorOnEntries` is hardcoded to
  uniform `Color32` — there is no place for a second stage.
- Bake writes `mesh.colors32` directly, so blends across stages are
  not expressible.

## 4. Target architecture

### 4.1 Core types

```csharp
internal interface IVertexColorBakeStage
{
    string DisplayName { get; }      // shown in the layer header
    string TypeId      { get; }      // stable id for serialization
    void   DrawSettings();           // own IMGUI block inside the layer foldout
    void   Compute(BakeContext ctx); // writes per-vertex Color32[] into ctx.Output
}

[System.Serializable]
internal sealed class BakeLayer
{
    public string  Name;                          // user-editable
    public bool    Enabled = true;
    public BlendOp BlendOp = BlendOp.Replace;
    [Range(0f, 1f)] public float Opacity = 1f;
    [SerializeReference] public IVertexColorBakeStage Stage;
    // future: per-layer mask (alpha multiplier sourced from another layer / channel)
    // future: channel mask (R-only / RGB-only writes)
}

[System.Serializable]
internal sealed class BakeLayerStack
{
    public Color           BaseColor = Color.white;   // canvas under all layers
    public List<BakeLayer> Layers    = new();         // [0] = bottom, [n-1] = top
}

internal sealed class BakeContext
{
    public IList<MeshEntry>             Entries;
    public Dictionary<Mesh, Color32[]>  Output;        // current stage writes here
    public Dictionary<Mesh, Color32[]>  Accumulated;   // composited so far (read-only for stage)
    public BakeContextCache             Cache;         // world positions, triangles, etc.
}

internal static class BakePipelineRunner
{
    public static void Run(BakeLayerStack stack, IList<MeshEntry> entries)
    {
        // 1. Initialize Accumulated[mesh] = filled with stack.BaseColor
        // 2. For each enabled layer, bottom → top:
        //      Compute(ctx) into ctx.Output
        //      Accumulated = Blend(Accumulated, Output, layer.BlendOp, layer.Opacity)
        // 3. One Undo.RecordObject per mesh + mesh.colors32 = Accumulated[mesh]
        //    EditorUtility.SetDirty(mesh)
    }
}
```

### 4.2 Blend ops

```csharp
internal enum BlendOp
{
    Replace,    // dst = src
    Multiply,   // dst = a * b
    Add,        // dst = a + b (clamped)
    Subtract,   // dst = a - b (clamped)
    Lerp,       // dst = lerp(a, b, opacity)  ← opacity already applied
    Screen,     // dst = 1 - (1 - a)*(1 - b)
    Overlay,    // photo-style overlay
    Min,
    Max,
}
```

`Replace`, `Multiply`, `Lerp` are the must-have set for the first
useful release. The rest land when a stage actually needs them.

### 4.3 Folder layout

```
Editor/Tools/VertexColorBaking/
  VertexColorBakingTool.cs        (UI host — owns the tab, hosts the active stack)
  BakeLayer.cs
  BakeLayerStack.cs
  BakeContext.cs
  BakePipelineRunner.cs
  BlendOps.cs                     (static blend math)
  StageRegistry.cs                (typeId → factory; powers "Add layer ▾" menu)
  Stages/
    IVertexColorBakeStage.cs
    SolidStage.cs
    GradientAxisStage.cs
    NoiseStage.cs
    TintStage.cs
    BlurStage.cs
    SubmeshStage.cs
    AOStage.cs                    (last to land — wraps VertexAOBaker)
  VariantExportPipeline.cs        (already exists; gets refactored to take a stack instead of a Color)
```

`VertexColorBakingTool.cs` keeps the AO branch intact during the
migration so AO users are never broken.

### 4.4 Stage catalog (planned)

| Stage              | Inputs                                    | Output                          |
|--------------------|-------------------------------------------|---------------------------------|
| `SolidStage`       | `Color`                                   | uniform RGBA per vertex         |
| `GradientAxisStage`| `Axis`, `UnityEngine.Gradient`, range     | RGBA along world/local axis     |
| `SubmeshStage`     | `List<(submeshIdx, Color)>`               | RGBA per submesh-owned vertex   |
| `NoiseStage`       | `Mode` (Random/Perlin/Voronoi), `Seed`, `Scale`, monochrome flag, range | per-vertex RGBA |
| `TintStage`        | `Color`, mode (Multiply / HueShift)       | full-stack tint adjustment      |
| `BlurStage`        | `Mode` (Topology / 3D), iterations, strength | smooths `ctx.Accumulated` and writes back; reuses `VertexAOBaker.BlurAO` / `BlurAO3D` |
| `AOStage` (later)  | full AO settings struct                   | wraps `VertexAOBaker` GPU/CPU path |

`BlurStage` is special: it reads `ctx.Accumulated` (everything below
it in the stack) and writes a smoothed version back. That makes the
stack `[Solid, Noise, Blur, Tint]` mean exactly what a user expects
("Tint is applied on top of a blurred Solid+Noise base").

### 4.5 Variants

```csharp
[System.Serializable]
internal sealed class VariantSpec
{
    public string         Suffix;
    public BakeLayerStack Stack;
}
```

`VariantExportPipeline.ExportVariants` takes `IList<VariantSpec>`. Per
variant: `BakePipelineRunner.Run(spec.Stack, entries)` → existing
`ExportVertexColorsToFbxAs` → existing `BuildPrefabClone`. Output paths
unchanged: `{base}_{suffix}.fbx`, `{base}_{suffix}.prefab`.

UI model: one "active" stack is editable on screen. `[Save as variant]`
deep-copies it into the variant list. Selecting a variant loads its
stack into the active editor. This avoids the complexity of
template + per-variant overrides.

### 4.6 Persistence

`[SerializeReference]` on `BakeLayer.Stage` lets Unity 6 serialize
polymorphic stage subclasses. The active stack lives on the tool until
the user opts in to persistence by saving to a sidecar asset:

```
Assets/Foo/TrainCarriage.fbx
Assets/Foo/TrainCarriage_uv2data.asset       ← existing UV2 sidecar
Assets/Foo/TrainCarriage_bakestack.asset     ← NEW: BakeLayerStack + variants
```

Decision deferred until the layered system has 2–3 working stages —
the schema needs real-world stages before we lock it in.

## 5. Roadmap

Each phase is one (or a few small) commits, AO branch stays usable
throughout. A user can stop the rollout at any phase boundary and ship
what they have.

| Phase | Title                         | Scope |
|-------|-------------------------------|-------|
| **A** | Scaffold + `SolidStage`       | Folder layout, `IVertexColorBakeStage`, `BakeLayer`, `BakeLayerStack`, `BakePipelineRunner`, `BlendOp`, `StageRegistry`. Re-implement Solid as a one-layer stack. UI behavior unchanged. |
| **B** | Layer Stack UI                | Multi-layer editor: list with `[+] [−] [↑] [↓]`, foldout per layer with blend op + opacity + stage settings. Active stack only (no variants yet). |
| **C** | Geometry-driven stages        | `GradientAxisStage`, `SubmeshStage`, `TintStage`. |
| **D** | Procedural & post stages      | `NoiseStage` (Random / Perlin / Voronoi, mono+colored, ranged), `BlurStage` (Topology + 3D, reuses `VertexAOBaker.BlurAO` / `BlurAO3D`). |
| **E** | Variants on stacks            | `VariantSpec` replaces the current `(Color, suffix)` struct. UI variant list with per-variant stack edit. `VariantExportPipeline` takes the new spec. |
| **F** | AO migration                  | `AOStage` wraps `VertexAOBaker`. Old AO branch in `OnDrawSidebar` removed. Tab becomes single-mode (always layer stack). |
| **G** | Sidecar persistence           | `BakeStackAsset` saved next to FBX / prefab. Optional auto-save on change, manual save button. |

## 6. Open decisions (to confirm before Phase A)

These were aligned in the design conversation but should be re-checked
when Phase A starts:

1. **`BlurStage` reads accumulated result, not its own input** — confirmed.
   Stack semantics: `[Solid, Noise, Blur, Tint]` = "tint on top of a
   blurred Solid+Noise base".
2. **AO migration is last (Phase F).** Keep the rich AO UI working
   until the layered system has been used for real.
3. **Variant = its own complete stack** (no shared template, no
   overrides). Copying between variants is via `[Save as variant]` /
   `[Duplicate]`.
4. **Persistence via sidecar asset** (not `EditorPrefs`). Schema is
   designed in Phase G when 2–3 stages exist; until then stacks are
   in-memory only and lost on tool close.
5. **`SerializeReference` for stages.** Requires Unity 2019.3+; the
   package targets `unity: "6000.0"` so this is safe.

## 7. Constraints to honor

These come from `CLAUDE.md` and apply to every phase:

- Namespace `SashaRX.UnityMeshLab` for all new files.
- `internal` visibility for cross-tool helpers.
- All scene / asset mutations through `Undo.RecordObject` (or
  `Undo.AddComponent` / `Undo.DestroyObjectImmediate` as appropriate).
- Logging via `UvtLog.Info` / `UvtLog.Warn` / `UvtLog.Error` with a
  bracketed prefix; new code uses `[Vertex Colors]`.
- FBX-Exporter code stays gated by `#if LIGHTMAP_UV_TOOL_FBX_EXPORTER`.
- `RestoreWorkingMeshes()` before switching LODGroup context.
- Temporary meshes (per-variant clones, blur scratch buffers) destroyed
  in `try/finally`.
- AssetDatabase batches wrapped in `StartAssetEditing` /
  `StopAssetEditing` (or `AssetEditingScope` on Unity 6+).
- No new external dependencies. Reuse `VertexAOBaker.BlurAO` /
  `BlurAO3D` for the blur stage.
- Collision meshes are skipped in every stage that paints geometry
  (`MeshHygieneUtility.IsCollisionNodeName`).

## 8. References

- Tab entry point: `Editor/Tools/VertexColorBakingTool.cs`
- Variant pipeline (current): `Editor/Tools/VariantExportPipeline.cs`
- FBX export plumbing: `Editor/Tools/LightmapTransferTool.cs`
  (`ExportVertexColorsToFbxCore`, `ExportVertexColorsToFbxAs`)
- Working-mesh / LOD context: `Editor/Framework/UvToolContext.cs`,
  `Editor/Framework/MeshEntry.cs`, `Editor/Framework/UvToolHub.cs`
- AO baker (will be wrapped in Phase F): `Editor/VertexAOBaker.cs`
- Collision detection helper: `MeshHygieneUtility.IsCollisionNodeName`
- Existing design docs in `Documentation~/`: `EXPERIMENTS.md`,
  `FBX_EXPORT_MODERNIZATION.md`.
