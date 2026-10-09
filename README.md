# Mesh Lab

Unity Editor tool suite for lightmap UVs, LODs, collision meshes, remeshing with material and lighting bakes, vertex colour baking, prefab assembly and mesh hygiene — in one window with a shared UV | 3D canvas.

Current release: **1.1.0** (see [CHANGELOG](CHANGELOG.md)).

## Overview

Open the window with **Tools → Mesh Lab → Open Mesh Lab**. The toolbar selects a tab; the sidebar shows that tab's controls; the canvas in the middle shows the current LODGroup's meshes as a UV layout or as a lit 3D view.

| Tab | Purpose |
|-----|---------|
| **UV2 Transfer** | Build a UV2 lightmap layout by repacking the UV0 shells of LOD0, then transfer it to the other LODs; export to FBX or the sidecar |
| **LOD Gen** | Generate LOD meshes with meshoptimizer simplification, UV2 preserved; create the LODGroup |
| **Remesh & Bake** | Experimental: voxel remesh → simplify → new UV0 → bake materials or the lit scene onto a fresh low-poly model, saved as FBX / mesh + material + prefab + maps |
| **Collision** | Collision meshes from any LOD: one simplified non-convex mesh, or a V-HACD convex decomposition |
| **UV1 Hierarchy** | Per-mesh UV1 repack across a GameObject subtree, with or without a LODGroup |
| **Prefab Builder** | Hierarchy, LOD levels, split / merge by material and collision of a prefab in one place, with edge and problem previews in the 3D view |
| **Cleanup** | Scan and fix mesh, material, collider and scene hygiene after UV, LOD, collision and FBX operations |
| **Vertex Color Baking** | Vertex AO (GPU hemisphere depth sampling) and Solid Color batch export with FBX + prefab variants |
| **Diagnostics** | Parameter sweep, multi-model benchmark, log filters, hierarchical probe, FBX metrics (debug UI only) |

## The canvas: UV | 3D

Every tab shares one canvas with a **UV | 3D** switch at its bottom centre (also `Tab` over the canvas), remembered across sessions.

* **UV** shows the preview LOD's UV layout: shells, wire, island borders, UDIM tiles, spot picking, a checker or texture background.
* **3D** is an orbitable lit view of the same meshes (drag orbits, middle button / alt-drag pans, scroll zooms, `F` or a middle double-click frames) with *Shaded* (scene materials), *Vert Colors*, *Normals*, *Tangents* and *UV0–UV3* shading, offered only where a mesh carries the data, a wire overlay and a Lit toggle.
* Tools feed the canvas: Remesh & Bake shows its stage result and cage there, Prefab Builder paints its edge classes and problem dots, and the result's UV layout is drawn on the model.

## UV2 Transfer

The UV2 pipeline preserves the existing UV0 shell structure and uses it as the basis for a new UV2 layout suitable for lightmapping.

* UV0 shells are extracted, packed with padding, and written to LOD0 as UV2
* UV2 is transferred to the other LODs by UV0 shell correspondence
* Seam-aware optimisation reduces artefacts from vertex splits

Best results come from LODs that keep a closely matching UV0 layout.

### Setup-tab pipeline

The **Setup** tab presents the pipeline as a numbered list of toggleable stages, each with its own settings underneath:

1. **Analyze UV0** — diagnose seams and shell count.
2. **Weld UV0** — merge false-seam vertices via source-guided weld.
3. **Symmetry Split** — split mirrored / overlapping UV0 shells in the source.
4. **Repack (xatlas)** — pack the source UVs into a clean UV2 atlas; auto-resolution from texel density by default.
5. **Transfer to LODs** — propagate UV2 onto every included target LOD.

**▶ Run Full Pipeline** executes every enabled stage in order, asynchronously: progress streams to the status bar and Unity's Background Tasks panel, and Cancel works mid-flight, including inside the xatlas pack and the shell transfer. Each stage row then shows `…` running, `✓` success, `✗` failed or `⏭` skipped.

### Saving

* **Apply UV2** writes the result to the working meshes.
* **Save to Sidecar** persists UV2 (and collision hulls) in a `_uv2data.asset` next to the FBX; the asset postprocessor replays it on reimport, so the FBX stays untouched.
* **Export FBX** writes the FBX with the new channels, the LOD hierarchy and the collision meshes, atomically, and relinks the scene afterwards.

## LOD Generation

* Generate LOD meshes from LOD0 with meshoptimizer simplification
* Configurable target ratios, error thresholds and attribute weights (UV2, normals)
* Detects LOD siblings (as siblings or children) and creates the LODGroup; a root that already carries a mesh is moved into a `_LOD0` child
* Generated LODs are saved as `.asset` files and optionally added to the LODGroup

## Remesh & Bake (experimental)

Build a new low-poly static model with fresh UV0 and reprojected maps from a high-poly source: static or posed skinned renderers, Read/Write-disabled meshes included (source importers are never touched).

* **Four re-runnable stages** — *Voxel remesh* (shape: LOD0 voxelisation, an oriented **Bounding box** per renderer, or a coarse voxel **Hull**), *Simplify* (error-driven, optional regularisation and small-part pruning), *Normals & UV* (Smooth / Angle / UV islands hard edges, xatlas chart and pack options), *Bake* (1–16 samples per texel, vertex colour / alpha transfer, vertex colour tinting the albedo). Each stage has 3D, UV and map previews.
* **Part filter** drops connected pieces under a size threshold or thinner than a cross-section threshold (pipes, cables, bolts) before any shape; **Highlight capture in Scene** paints what the remesh will see, live with the sliders.
* **Materials bake** reprojects base colour, normal, metallic/smoothness, occlusion and emission from Standard and URP Lit materials (other shaders bake from common property names with a warning).
* **Beauty bake** bakes the lit view: the renderer's lightmaps (RGBM / HDR / Bakery decoded, directional response), realtime and mixed lights with hard ray shadows that also test the rest of the scene's shadow casters, light culling masks, Subtractive mixed lighting, ambient and reflection probes blended as the runtime blends them. Beauty results save with the pipeline's unlit shader.
* **Keep hierarchy** remeshes and bakes every renderer as its own node and saves a rebuilt root prefab; the weld saves one `_LOD0` mesh.
* **Save** writes an FBX (or mesh asset) + material + prefab + BaseColor / Normal / MetallicSmoothness / Occlusion PNGs and an HDR Emission EXR into a new folder; Built-in and URP.

Native remeshing comes from meshoptimizer v1.3 (pinned by commit) through the bridge in `Native~/`; the **Build Native Libraries** workflow builds and tests it on all three platforms and commits the binaries. See [workflow, limitations and validation](Documentation~/REMESH_AND_BAKE.md).

## Collision Mesh Generation

Two modes for generating physics collision meshes from any LOD:

### Simplified (non-convex)
* Aggressively simplifies the source mesh via meshoptimizer
* Uses only positions and triangles: ignores normals, tangents, vertex colours and all UV channels, merges material submeshes, and welds exactly coincident vertices before simplification. The source mesh stays unchanged.
* Creates a single `MeshCollider(convex=false)` — for static objects, terrain, walls
* Configurable target ratio and error tolerance

### Convex Decomposition (V-HACD)
* Decomposes the mesh into multiple convex hulls with V-HACD 4.1
* Creates compound `MeshCollider(convex=true)` — for dynamic / kinematic objects
* Full V-HACD parameter control: max hulls, resolution, verts per hull, fill mode, recursion depth, shrink wrap, best plane search
* Generated surfaces and per-hull wireframe in the shared **3D** preview; **Source + collision wire** compares against the original. Scene View also shows the generated edges.
* Hulls are capped at 128 vertices and checked against Unity's 255-triangle convex limit.

### Collision persistence
* **Apply to Scene** — creates `_COL` GameObjects with MeshCollider components, preserves nested source transforms and the root's layer, and supports Undo/Redo. Remove existing collision objects before applying a replacement.
* **Save to Sidecar** — persists source-local hulls in `_uv2data.asset` for FBX reimport. Direct document saves preserve the source node's placement; rebuilt exports convert hulls using the source frame captured before hierarchy normalization.
* **FBX Export** — collision meshes are included automatically when exporting FBX

## UV1 Hierarchy

Per-mesh UV1 pack or repack across a GameObject subtree, no LODGroup required: every included MeshRenderer under the selected root gets a fresh unique UV1 atlas on a clone; **Apply** swaps the clones into the scene, **Overwrite Selected FBX** re-saves the FBX with the new channel through the isolated channel export.

## Prefab Builder

One tab for assembling a prefab: **Hierarchy** (rebuild the LODGroup from `_LOD{n}` names, move a root mesh into a LOD0 child), **LOD Levels**, **Split / Merge** by material (every vertex attribute carried, static flags and renderer settings kept), **Collision** (collider from the collision child) and a **Build Pipeline** that runs them in order. Its *Edges* and *Problems* previews colour edge classes and unused vertices in the Scene view and in the 3D canvas.

## Cleanup

Scan and fix the hygiene issues the other operations leave behind: **Mesh** (empty channels, attribute set, duplicate material slots, Read/Write handling without importer flips), **Colliders** (stray attributes on collider meshes), **Split / Merge** (same-material merge, per-material split), import settings. Every fix records Undo.

## Vertex Color Baking

A single tab with two bake modes selected by the toolbar at the top:

### AO mode

GPU-accelerated per-vertex ambient occlusion via hemisphere depth sampling:

* Renders depth maps from multiple hemisphere directions around each vertex
* A compute shader accumulates occlusion from depth comparisons
* Configurable sample count, radius, bias, normal offset and intensity
* CPU fallback for platforms without compute shader support
* Results written to a vertex colour component or a UV component, with blur and levels

### Solid Color mode

Batch export of colour variants from one source FBX / prefab:

* Edit a list of `(Color, suffix)` rows; `+ Add variant` to append, `−` to remove
* `Bake (preview)` paints `variants[0]` onto the working meshes for in-editor inspection without writing files
* `Bake & Export All` runs the full pipeline per variant: paint the colours, export `{base}_{suffix}.fbx`, instantiate the source prefab, swap the mesh references to the new sub-meshes by name, and save `{base}_{suffix}.prefab` (a full clone, not a Prefab Variant); the source meshes are restored afterwards
* Collision meshes (`_COL`, `_COL_Hull{N}`, `_Collider`, `_Collision`) are skipped by the shared naming rules
* Existing files are overwritten — use git to roll back unwanted variants

## Diagnostics and debug surfaces

The **Diagnostics** tab (parameter sweep, multi-model benchmark, report rebuild, log filters, hierarchical probe, FBX metrics export) and the other debug surfaces (UV0 Analysis & Fix in UV2 Transfer, the FBX Metrics menus, the Sweep Test Suite asset) are hidden by default. Enable them with **Edit → Project Settings → Mesh Lab → Developer → Show Debug UI**. The same settings page holds the project-wide defaults (sidecar mode, repack per mesh, the default AO target channel).

## Key characteristics

* **Repack, not full unwrap** — preserves the existing UV0 shell structure
* **LOD-aware UV transfer** — transfers UV2 through UV0 shell correspondence
* **Collision from any LOD** — collision meshes from the source LOD geometry
* **Non-destructive by default** — results live in Unity assets and the `_uv2data.asset` sidecar; FBX writes are explicit and atomic
* **One canvas for everything** — the UV layout and the lit 3D view of the same meshes, fed by every tab
* **Remesh & Bake** — a new low-poly model with materials or the lit scene baked onto it
* **Diagnostics included** — transfer quality, shell visualisation, bake health, benchmarks

## Architecture

The editor assembly `SashaRX.UnityMeshLab.Editor` (namespace `SashaRX.UnityMeshLab`) is one hub, nine tabs and a set of libraries the tabs share:

| Folder | Holds |
|--------|-------|
| `Editor/Framework/` | `UvToolHub` (the window), `UvToolContext` (shared state), `UvCanvasView` (UV canvas), `MeshViewport3D` (3D view), `IUvTool` / `IUvTool3D` |
| `Editor/Tools/` | The tabs, one `IUvTool` each |
| `Editor/Mesh/` | `MeshNaming` (the one reader of LOD / collision naming rules), `MeshSplitMerge`, `MeshAccess` (reading Read/Write-disabled meshes), `MeshTransform`, `LodHierarchy`, `VertexChannels`, `RendererSettings` |
| `Editor/Uv/` | `UvTopology` (boundary edges, shells, point-in-triangle, UDIM tiles) |
| `Editor/Geometry/` | `MeshGeometry`, `GpuReadback`; the BVHs in `Editor/TriangleBvh*.cs` |
| `Editor/Assets/` | `FbxExport` (every FBX write), `SidecarStore` (the `_uv2data.asset` sidecar), `TextureAssets` |
| `Editor/Bench/` | `SweepRunner`, `BenchmarkRunner`, recorder, sweep reports, test suite asset |
| `Editor/Diagnostics/` | `DebugUi`, `BakeHealth`, `HierarchicalDiag`, `FbxMetricsExporter` |
| `Editor/Remesh*.cs` | The Remesh & Bake pipeline, source capture, baker, Beauty lighting, exporter, previews, native bridge |
| `Native~/`, `Plugins/` | C++ bridge (xatlas, meshoptimizer, V-HACD) and the prebuilt binaries |

`AGENTS.md` and `CLAUDE.md` describe the conventions for contributors and AI agents.

## Dependencies

| Library | Role | License |
|---------|------|---------|
| [xatlas](https://github.com/jpcy/xatlas) | UV chart packing | MIT |
| [meshoptimizer v1.3](https://github.com/zeux/meshoptimizer) | Simplification, seam-aware optimisation, vertex weld, voxel remesh | MIT |
| [V-HACD 4.1](https://github.com/kmammou/v-hacd) | Convex decomposition for collision meshes | BSD-3-Clause |
| [FBX Exporter](https://docs.unity3d.com/Packages/com.unity.formats.fbx@latest) (`com.unity.formats.fbx`) | FBX writes; the FBX code compiles behind `LIGHTMAP_UV_TOOL_FBX_EXPORTER`, set when the package is present | Unity Companion |

This repository is licensed under **MIT**. All vendored dependencies are MIT / BSD compatible.

## Installation

### Unity Package Manager

1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL...**
3. Enter:

```text
https://github.com/SashaRX/UnityMeshLab.git
```

### Manual installation

Clone the repository into your project's `Packages/` folder:

```bash
cd YourProject/Packages
git clone https://github.com/SashaRX/UnityMeshLab.git com.sasharx.unitymeshlab
```

## Usage

1. Open **Tools → Mesh Lab → Open Mesh Lab**
2. Select a `LODGroup` in the scene, or a GameObject with LOD children (LOD Gen and Prefab Builder can create the group)
3. Use the tabs for your workflow:
   - **UV2 Transfer**: Setup tab — toggle the stages (Analyze / Weld / SymSplit / Repack / Transfer) → **▶ Run Full Pipeline** → Apply UV2 / Save to Sidecar / Export FBX; Repack tab — standalone repack iteration; Transfer tab — per-LOD quality report
   - **LOD Gen**: configure ratios → Generate LODs (or create the LODGroup from the renderers)
   - **Remesh & Bake**: select the source root → run the stages in order (each re-runnable) → Save
   - **Collision**: choose the mode → Generate → Apply to Scene / Save to Sidecar
   - **UV1 Hierarchy**: pick the subtree → Repack → Apply or Overwrite Selected FBX
   - **Prefab Builder** / **Cleanup**: Scan → review → Fix
   - **Vertex Color Baking**: AO mode — configure samples → Bake → Apply; Solid Color mode — add `(Color, suffix)` variants → Bake & Export All

## Requirements

* Unity 6 (6000.0) or newer
* `com.unity.formats.fbx` (installed as a package dependency) for FBX writes
* Prebuilt native libraries included for Windows x64, Linux x64 and macOS (universal)

## Development

### Checks that run without a Unity licence

```bash
python3 Tools~/compile_check.py                            # Editor + Tests against Unity reference assemblies, with and without the FBX define
python3 Tools~/check_undeclared_identifiers.py Editor Tests  # CS0103 heuristic
node --test Tools~/sonar-pr-check.test.mjs                 # the Sonar PR-check tooling
```

The NUnit suites in `Tests/Editor/` run in the Unity Test Runner (EditMode); the pure-logic ones also run outside Unity.

### Continuous integration

| Workflow | Does |
|----------|------|
| **Unity Package Checks** | `.meta` coverage, `package.json`, the CS0103 guard and the licence-free C# compile on every push and PR |
| **Unity Tests** | EditMode tests when a Unity licence is configured |
| **SonarQube** | Scans `main` into the self-hosted server with the quality gate; scans every PR into a throwaway project and reports the findings on the lines it changed (rule policy with reasons in the workflow and `Tools~/sonar-pr-check.mjs`) |
| **Build Native Libraries** | Builds and tests the C++ bridge on Windows, Linux and macOS and commits the binaries on changes to `Native~/` |
| **Auto Version Bump** / **Release** | Patch bump on `main` pushes that did not set a version themselves; a `v*` tag publishes the GitHub release from the matching CHANGELOG section |

### Building the native library

The repository includes prebuilt native libraries. To rebuild locally:

```bash
cmake -S Native~ -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
ctest --test-dir build
```

Requirements: CMake 3.20+, C++17 compiler. xatlas and V-HACD are vendored in `Native~/third_party/`; meshoptimizer is fetched by CMake FetchContent at the pinned commit.

## Documentation

* [Remesh & Bake](Documentation~/REMESH_AND_BAKE.md) — workflow, architecture, limits, validation checklist
* [Transfer pipeline experiments](Documentation~/EXPERIMENTS.md) and [benchmark protocol](Documentation~/TRANSFER_BENCHMARK.md)
* [FBX pipeline checklist](Documentation~/FBX_PIPELINE_CHECKLIST.md)
* [Vertex Color Baking architecture](Documentation~/VERTEX_COLOR_BAKING_ARCHITECTURE.md)
* [CHANGELOG](CHANGELOG.md)

## License

MIT
