# Lightmap UV Tool

Unity Editor tool for transferring UV2 lightmap coordinates from LOD0 to LOD1+ meshes via xatlas.

## What it does

- Takes a LODGroup (or set of LOD meshes)
- Repacks existing UV0 shells into UV2 for lightmap (no full unwrap — repack only)
- Resolves UV0 overlaps so each shell gets a unique lightmap chart
- Transfers the resulting UV2 from LOD0 to all other LODs via surface correspondence
- Never modifies geometry, never adds vertices, never creates new seams

## Current status

**Stage 1 — Complete:**
- xatlas native bridge (C++ DLL with P/Invoke wrapper)
- UV shell extraction via Union-Find
- Overlap group detection via bbox intersection
- xatlas AddUvMesh repack pipeline (repack-only, no unwrap)
- C#-side UV2 assignment with majority vote conflict resolution
- Orphan vertex detection and snap-to-midpoint fix
- UV Preview window (GL rendering with shell fill, wireframe, degenerate overlay)

**Next: Stage 2** — Source mesh analysis (BVH), surface projection, shell-aware transfer to LOD1.

## Structure

```
Assets/
  Editor/
    LightmapUvTool/
      XatlasRepack.cs         — Main repack pipeline
      XatlasNative.cs         — P/Invoke declarations
      UvShellExtractor.cs     — Shell extraction + overlap detection
      XatlasRepackTest.cs     — Menu test entry point
      UvPreviewWindow.cs      — UV preview editor window
  Plugins/
    x86_64/
      xatlas-unity.dll        — Compiled native bridge (not in repo)
Native/
  xatlas-unity-bridge.cpp     — Native bridge source
  xatlas.h                    — xatlas header (not in repo, get from github.com/jpcy/xatlas)
  xatlas.cpp                  — xatlas impl (not in repo)
```

## Building the native bridge

```
cl /O2 /LD /EHsc xatlas-unity-bridge.cpp xatlas.cpp /Fe:xatlas-unity.dll
```

Place resulting `xatlas-unity.dll` into `Assets/Plugins/x86_64/`.

## Usage

1. Select a GameObject with MeshFilter
2. `Tools → Xatlas → Test Repack Selected` — builds UV2 on the selected mesh
3. `Tools → Xatlas → UV Preview` — opens UV preview window (supports UV0/UV2 toggle)

## Constraints

- Uses xatlas **AddUvMesh** (repack mode only) — no full unwrap
- Unity's `GenerateSecondaryUVSet` is not used
- Topology is never modified
- Only UV2 channel is written
