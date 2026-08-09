# Mesh Lab

Unity Editor tool suite for lightmap UV management, LOD generation, and collision mesh generation — all in one window.

## Overview

Mesh Lab provides six integrated tools accessible via **Tools → Mesh Lab**:

| Tab | Purpose |
|-----|---------|
| **UV2 Transfer** | Generate UV2 lightmap layouts by repacking UV0 shells, then transfer across LODs |
| **Atlas Pack** | Multi-model atlas packing |
| **UV0 Optimize** | UV0 analysis and optimization |
| **LOD Gen** | Generate LOD meshes via meshoptimizer simplification with UV2 preservation |
| **Collision** | Generate collision meshes — simplified (non-convex) or V-HACD convex decomposition |
| **Vertex Color Baking** | Vertex AO (GPU hemisphere depth sampling) and Solid Color batch export with FBX + Prefab variants |

## UV2 Transfer

The UV2 pipeline preserves the existing UV0 shell structure and uses it as the basis for building a new UV2 layout suitable for lightmapping.

* UV0 shells are extracted, packed with padding, and written to LOD0 as UV2
* UV2 is then transferred to other LODs by matching UV0 shell correspondence
* Seam-aware optimization reduces artifacts from vertex splits

Best results are achieved when all LODs preserve a closely matching UV0 layout.

### Setup-tab pipeline

The **Setup** tab presents the pipeline as a numbered list of toggleable stages, each with its own settings nested directly underneath:

1. **Analyze UV0** — diagnose seams and shell count (cheap diagnostic).
2. **Weld UV0** — merge false-seam vertices via source-guided weld (recommended).
3. **Symmetry Split** — split mirrored / overlapping UV0 shells in the source.
4. **Repack (xatlas)** — pack source UVs into a clean UV2 atlas. Auto-resolution from texel density is the default — pick a target tex/m and the tool derives atlas size from total 3D area.
5. **Transfer to LODs** — propagate UV2 onto every included target LOD.

A single **▶ Run Full Pipeline** button executes every enabled stage in order. The async path keeps the editor responsive: progress streams to the bottom status bar (and Unity's Background Tasks panel) without ever raising the "Hold on" busy dialog. Cancel mid-flight via the strip's Cancel button — even during xatlas pack and shell transfer.

After a run, each stage row shows the outcome with an icon: `…` running, `✓` success, `✗` failed, `⏭` skipped. While idle, the bottom strip shows the last operation summary (`✓ Run Full Pipeline · 12.3s`).

### Debug surfaces

Diagnostic and benchmarking blocks (Parameter Sweep, Log Filters, UV0 Analysis & Fix, FBX Metrics menus, Sweep Test Suite asset) are hidden by default. Enable them via **Edit → Project Settings → Mesh Lab → Developer → Show Debug UI** when iterating on the pipeline or debugging.

## LOD Generation

* Generate LOD meshes from LOD0 using meshoptimizer simplification
* Configurable target ratios, error thresholds, and attribute weights (UV2, normals)
* Auto-detect LOD siblings (as siblings or children) and create LODGroup automatically
* Generated LODs are saved as `.asset` files and optionally added to the LODGroup

## Collision Mesh Generation

Two modes for generating physics collision meshes from any LOD:

### Simplified (non-convex)
* Aggressively simplifies the source mesh via meshoptimizer
* Creates a single `MeshCollider(convex=false)` — suitable for static objects, terrain, walls
* Configurable target ratio and error tolerance

### Convex Decomposition (V-HACD)
* Decomposes the mesh into multiple convex hulls using V-HACD 4.1
* Creates compound `MeshCollider(convex=true)` — suitable for dynamic/kinematic objects
* Full V-HACD parameter control: max hulls, resolution, verts per hull, fill mode, recursion depth, shrink wrap, best plane search
* Scene wireframe preview of generated hulls

### Collision persistence
* **Apply to Scene** — creates `_COL` GameObjects with MeshCollider components
* **Save to Sidecar** — persists collision data in `_uv2data.asset` for FBX reimport
* **FBX Export** — collision meshes are automatically included when exporting FBX

## Auto LODGroup Creation

When no LODGroup exists, the Setup tab and LOD Gen tab detect LOD siblings automatically:

* **LOD-named children** (`Chair_LOD0`, `Chair_LOD1`) — detected and listed, one-click "Add LOD Group" creates correct multi-level LODGroup
* **No LOD naming** — fallback detects any child renderers, creates LODGroup with all renderers as LOD0, renames children to `_LOD0` for consistency
* **Auto-clear stale context** — selecting a new mesh object automatically clears the previous LODGroup reference

After creation, use LOD Gen to generate lower LODs and the naming (`_LOD1`, `_LOD2`) is handled automatically.

## Vertex Color Baking

A single tab with two bake modes selected by toolbar at the top:

### AO mode

GPU-accelerated per-vertex ambient occlusion via hemisphere depth sampling:

* Renders depth maps from multiple hemisphere directions around each vertex
* Compute shader accumulates occlusion from depth comparisons
* Configurable sample count, radius, bias, normal offset, and intensity
* CPU fallback for platforms without compute shader support
* Results written to vertex colors or UV channels

### Solid Color mode

Batch export of color variants from one source FBX/prefab:

* Edit a list of `(Color, suffix)` rows; `+ Add variant` to append, `−` to remove
* `Bake (preview)` paints `variants[0]` onto working meshes for in-editor inspection without writing files
* `Bake & Export All` runs the full pipeline per variant: paint `mesh.colors32`, export `{base}_{suffix}.fbx`, instantiate the source prefab, swap `MeshFilter.sharedMesh` references to the new sub-meshes by name, and save `{base}_{suffix}.prefab` (full clone, not a Prefab Variant)
* Collision meshes are skipped automatically via `MeshHygieneUtility.IsCollisionNodeName`
* Existing files are overwritten — use git to roll back unwanted variants

## Key characteristics

* **Repack, not full unwrap** — preserves existing UV0 shell structure
* **LOD-aware UV transfer** — transfers UV2 through UV0 shell correspondence
* **Collision from any LOD** — generate collision meshes from source LOD geometry
* **Dual save** — results persist as Unity assets and in FBX exports
* **Vertex Color Baking** — AO (GPU hemisphere depth sampling) and Solid Color batch FBX + Prefab variant export
* **Diagnostics included** — transfer quality, shell visualization, wireframe preview

## Dependencies

| Library | Role | License |
|---------|------|---------|
| [xatlas](https://github.com/jpcy/xatlas) | UV chart packing | MIT |
| [meshoptimizer](https://github.com/zeux/meshoptimizer) | Mesh simplification, seam-aware optimization, vertex weld | MIT |
| [V-HACD 4.1](https://github.com/kmammou/v-hacd) | Convex decomposition for collision meshes | BSD-3-Clause |

This repository is licensed under **MIT**. All dependencies are MIT/BSD compatible.

## Installation

### Unity Package Manager

1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL...**
3. Enter:

```text
https://github.com/SashaRX/UnityLodUvLightmapTransfer.git
```

### Manual installation

Clone the repository into your project's `Packages/` folder:

```bash
cd YourProject/Packages
git clone https://github.com/SashaRX/UnityLodUvLightmapTransfer.git com.sasharx.lightmap-uv-tool
```

## Usage

1. Open **Tools → Mesh Lab**
2. Select a `LODGroup` in the scene (or select a GameObject with LOD children — LOD Gen tab can auto-create the group)
3. Use the tabs for your workflow:
   - **UV2 Transfer**:
     - Setup tab: toggle the pipeline stages you want (Analyze / Weld / SymSplit / Repack / Transfer) → **▶ Run Full Pipeline** → Apply UV2 / Export FBX from the sidebar footer
     - Repack tab: standalone Repack iteration — Resolution / Pack Quality / Density / Compression sections
     - Transfer tab: per-LOD quality report after a transfer run
   - **LOD Gen**: Configure ratios → Generate LODs (or auto-create LODGroup from renderers)
   - **Collision**: Choose mode → Generate → Apply to Scene / Save to Sidecar
   - **Vertex Color Baking**: AO mode — Configure samples → Bake → Apply to vertex colors/UVs. Solid Color mode — Add `(Color, suffix)` variants → Bake & Export All to write `_Red.fbx` + `_Red.prefab` per variant

## Requirements

* Unity 6 (6000.0) or newer
* Prebuilt native libraries included for Windows x64, Linux x64, and macOS (universal)

## Building the native library

The repository includes prebuilt native libraries. To rebuild:

```bash
cmake -S Native -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Requirements: CMake 3.20+, C++17 compiler. xatlas and V-HACD are vendored in `Native~/third_party/`; meshoptimizer is fetched automatically via CMake FetchContent.

GitHub Actions CI automatically builds for Windows, Linux, and macOS on changes to `Native/`.

## License

MIT
