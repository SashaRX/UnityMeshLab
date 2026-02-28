# Lightmap UV LOD Transfer

Unity Editor tool for transferring UV2 lightmap coordinates from LOD0 to higher LOD levels using xatlas repack and surface-based projection.

## Features

- **Repack, not unwrap**: uses existing UV0 shells as input charts for xatlas packing
- **Surface-based transfer**: projects target LOD geometry onto LOD0 surface, not UV-space
- **Shell-aware pipeline**: shell classification → isolated transfer → border repair
- **No topology changes**: only UV coordinates are modified, no vertices added or split
- **Multi-mesh atlas**: supports multiple renderers sharing one lightmap atlas
- **Quality diagnostics**: per-triangle status (Accepted/Ambiguous/BorderRisk/Mismatch), confidence heatmaps, border repair reports
- **Editor UI**: 8-stage pipeline with individual re-run, UV preview with 7 visualization modes

## Installation

### Unity Package Manager (recommended)

1. Open **Window → Package Manager**
2. Click **+** → **Add package from git URL...**
3. Enter:
   ```
   https://github.com/SashaRX/UnityLodUvLightmapTransfer.git
   ```

### Manual

Clone the repo into your project's `Packages/` folder:
```bash
cd YourProject/Packages
git clone https://github.com/SashaRX/UnityLodUvLightmapTransfer.git com.sasharx.lightmap-uv-tool
```

## Usage

1. Open **Tools → Lightmap UV → Transfer Window**
2. Select a LODGroup in the scene
3. Configure settings (UV channels, atlas resolution, padding, projection parameters)
4. Click **Run Full Pipeline** or execute stages individually
5. Review results in the UV preview and quality report
6. Save output mesh assets

## Building Native DLL

The pre-built `xatlas-unity.dll` is included for Windows x64. To rebuild:

```bash
build_native.bat
```

Requirements: Visual Studio 2022, CMake 3.20+. xatlas source is fetched automatically.

## Pipeline Stages

| Stage | Description |
|-------|-------------|
| 0. Collect | Gather meshes from LODGroup |
| 1. Repack | Build UV2 on LOD0 via xatlas chart packing |
| 2. Analyze | Build source BVH, shells, borders, metrics |
| 3. Shell Assign | Project target onto source, classify shell membership |
| 4. Initial Transfer | Shell-isolated UV transfer |
| 5-6. Border Repair | Detect border primitives, measure perimeter, conditional fuse |
| 7. Validate | Quality evaluation, triangle status classification |
| 8. Save | Write mesh assets, update LODGroup |

## Requirements

- Unity 2020.3+
- Windows x64 (native DLL)

## License

MIT
