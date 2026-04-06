# Lightmap UV LOD Transfer

Unity Editor tool for generating UV2 lightmap layouts by repacking existing UV0 shells, then transferring the result across LOD levels through UV0 shell correspondence.

## Overview

This tool does **not** unwrap meshes from scratch.
Instead, it preserves the existing UV0 shell structure and uses it as the basis for building a new UV2 layout suitable for lightmapping.

The workflow is designed for assets where LOD meshes keep a broadly similar UV0 layout. In that case, UV2 generated on LOD0 can be transferred to other LOD levels by matching corresponding UV0 shells.

## How it works

### UV2 generation on LOD0

The tool builds UV2 from the existing UV0 layout:

* UV0 shells are extracted and treated as chart input
* Overlapping UV0 regions are resolved by creating separate UV2 chart instances where needed
* Padding is added between packed charts
* The resulting packed layout is written to LOD0 as UV2

This preserves the original shell structure while producing a unique, non-overlapping UV2 layout for lightmapping.

### UV2 transfer to higher LODs

After UV2 is generated for LOD0, it is transferred to the remaining LOD meshes:

* UV0 shells on target LODs are matched against LOD0
* UV2 is transferred shell-by-shell using UV0 correspondence
* Additional seam-aware optimization is used during transfer to reduce artifacts caused by vertex splits

Best results are achieved when all LODs preserve a closely matching UV0 layout.

## Key characteristics

* **Repack, not full unwrap** — preserves existing UV0 shell structure instead of generating a new unwrap
* **LOD-aware UV transfer** — transfers UV2 through UV0 shell correspondence rather than topology identity
* **No final topology changes** — only UV2 data is modified in the output meshes
* **Supports shared atlases** — multiple renderers can be packed into the same lightmap atlas
* **Diagnostics included** — transfer quality can be inspected through per-triangle status, confidence views, and repair reports

## Limitations

* The transfer step depends on UV0 similarity between LOD levels
* Results may degrade if UV0 shells differ too much between LODs
* The tool is intended for repacking and transferring authored shell layouts, not for fully automatic unwrap generation from arbitrary topology

## Dependencies

| Library                                                | Role                                          | License |
| ------------------------------------------------------ | --------------------------------------------- | ------- |
| [xatlas](https://github.com/jpcy/xatlas)               | UV chart packing                              | MIT     |
| [meshoptimizer](https://github.com/zeux/meshoptimizer) | Seam-aware optimization / vertex weld support | MIT     |

This repository is also licensed under **MIT**.

## Features

* **UV0-based lightmap repacking**
* **UV2 transfer across LODs**
* **Seam-aware processing to reduce transfer artifacts**
* **Multi-mesh atlas support**
* **Quality diagnostics and validation views**
* **Editor UI with staged pipeline execution**

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
2. Select a `LODGroup` in the scene
3. Configure atlas size, padding, and transfer settings
4. Run the full pipeline or execute stages individually
5. Inspect the generated UVs and validation results
6. Save the output mesh assets

## Pipeline stages

| Stage               | Description                                                          |
| ------------------- | -------------------------------------------------------------------- |
| 0. Collect          | Gather meshes from the selected `LODGroup`                           |
| 1. Repack           | Generate UV2 on LOD0 by packing UV0-derived shells                   |
| 2. Analyze          | Build shell data, borders, metrics, and transfer helpers             |
| 3. Shell Assign     | Match UV0 shells between LOD0 and target LODs                        |
| 4. Initial Transfer | Transfer UV2 to target meshes shell-by-shell                         |
| 5–6. Border Repair  | Detect border issues and conditionally repair seam-related artifacts |
| 7. Validate         | Evaluate transfer quality and classify triangle status               |
| 8. Save             | Write mesh assets and update the `LODGroup`                          |

## Requirements

* Unity 2020.3+
* Windows x64 for the included prebuilt native DLL

## Building the native DLL

The repository includes a prebuilt `xatlas-unity.dll` for Windows x64.
To rebuild it:

```bash
build_native.bat
```

Requirements:

* Visual Studio 2022
* CMake 3.20+

The xatlas source is fetched automatically during the build process.

## License

MIT
