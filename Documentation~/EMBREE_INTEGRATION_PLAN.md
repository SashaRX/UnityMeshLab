# Embree Integration Plan

Plan for adding [Intel Embree 4](https://github.com/RenderKit/embree) as an
optional native acceleration backend for transfer, AO baking, and future SDF
generation. Branch: `claude/add-embree-sdf-PwoX3`.

## Why

Three existing pipelines need fast spatial queries on triangle meshes:

1. **Transfer** (`TransferData.cs`, `GroupedShellTransfer.cs`) — closest-point
   from LOD_N vertices onto LOD_0 surface, plus barycentric interpolation of
   UV2 / vertex attributes. Currently uses managed `TriangleBvh.cs` (512 LoC).
2. **Vertex AO** (`VertexAOBaker.Cpu.cs`, `.Gpu.cs`) — hemisphere ray casts per
   vertex against scene geometry. CPU path is single-threaded managed BVH;
   GPU path is a DX11/Vulkan ComputeShader.
3. **SDF generation** (planned) — signed-distance field grids for
   collision/voxelization workflows.

All three are textbook Embree use-cases. Embree gives 3–10× over managed BVH
for AO (SIMD ray packets + mature SAH BVH) and removes the GPU dependency for
users without a capable Compute backend.

## Scope

- **Optional** native backend, gated by `UNITYMESHLAB_EMBREE` scripting define.
- Without the binary, the package builds and runs exactly as today
  (managed `TriangleBvh` + `VertexAOBaker.Cpu`/`.Gpu` fallbacks).
- **Platforms (initial):** Windows x64, Linux x64.
- **Platforms (deferred):** macOS (Apple Silicon arm64). No dev hardware
  available — fallback to managed BVH + existing Metal-transpiled GPU AO.

## Non-goals

- Replacing the managed `TriangleBvh` outright. It stays as the default
  fallback and the source of truth for behaviour.
- Wrapping every Embree feature. We expose only point-query, occlusion-rays,
  and SDF-grid entry points.
- Building Embree from source inside Unity. Native lib is built separately
  via `Native~/Embree/build_embree.{bat,sh}` and the result lives in
  `Plugins/x86_64/`.

## Architecture

### Native artifact

Separate shared library **`umlab-embree`**, NOT merged with the existing
`xatlas-unity` plugin. Embree + TBB add ~30–50 MB and many users won't
need the Embree backend.

```
Plugins/x86_64/
  xatlas-unity.dll            (existing)
  xatlas-unity.so             (existing)
  umlab-embree.dll            (new)
  umlab-embree.so             (new)
  tbb12.dll                   (new, runtime dep of Embree)
  libtbb.so.12                (new)
```

### Source layout

```
Native~/
  CMakeLists.txt              (existing, untouched — xatlas-unity)
  xatlas-unity-bridge.cpp     (existing)
  src/collision.cpp           (existing)
  third_party/                (existing — VHACD)
  Embree/                     (NEW)
    CMakeLists.txt            FetchContent embree v4.3+, statically link
                              where possible, copy DLL/so to Plugins/x86_64/
    umlab-embree.h            C ABI header
    umlab-embree.cpp          C ABI implementation
    build_embree.bat          Win64 release build helper
    build_embree.sh           Linux x64 release build helper
    third_party/
      LICENSE-EMBREE.txt      Apache 2.0 attribution
      LICENSE-TBB.txt         Apache 2.0 attribution
```

### C# layout

```
Editor/
  Native/                     (NEW folder — colocated P/Invoke wrappers)
    EmbreeNative.cs           [DllImport("umlab-embree")] declarations
    EmbreeScene.cs            IDisposable handle wrapper, cache by mesh id
    IBvhBackend.cs            Common interface (closest-point, raycast)
  TriangleBvh.cs              (existing) → implements IBvhBackend
  EmbreeBvh.cs                (NEW)      → implements IBvhBackend
  BvhBackendFactory.cs        (NEW)      Selects Embree if available, else managed
  VertexAOBaker.cs            (existing) Add Backend.Embree enum value
  VertexAOBaker.Embree.cs     (NEW)      Sibling of .Cpu / .Gpu
```

### Define management

Extend `Editor/PostprocessorDefineManager.cs`:

- On editor load, check for presence of `Plugins/x86_64/umlab-embree.dll` (Win)
  or `Plugins/x86_64/umlab-embree.so` (Linux).
- If present: add `UNITYMESHLAB_EMBREE` to `PlayerSettings` scripting defines
  (Editor platform group). If absent: remove it.
- Mirrors the existing pattern for `UNITY_MESH_LAB_FBX_EXPORTER`.

## C ABI surface

Minimal, batched, handle-based. Single P/Invoke per thousands of points to
avoid marshalling overhead.

```c
// ── Scene lifecycle ──
typedef struct EmbreeSceneOpaque* uml_embree_scene_t;

uml_embree_scene_t uml_embree_scene_create(void);
void uml_embree_scene_destroy(uml_embree_scene_t);

// Returns geom_id (>=0) on success, -1 on failure.
int uml_embree_scene_add_mesh(
    uml_embree_scene_t scene,
    const float* vertices, int vertex_count,   // xyz, tightly packed
    const int*   triangles, int tri_count);    // i0,i1,i2 per tri

void uml_embree_scene_commit(uml_embree_scene_t);   // builds BVH

// ── Transfer: closest-point batch ──
// For each query point, finds closest point on any committed mesh.
void uml_embree_closest_points(
    uml_embree_scene_t scene,
    const float* points, int point_count,       // input: xyz per point
    float* out_positions,                       // xyz, hit position
    float* out_normals,                         // xyz, surface normal
    int*   out_geom_ids,                        // -1 if no hit
    int*   out_prim_ids,
    float* out_barycentrics);                   // u, v per hit (w = 1-u-v)

// ── AO bake batch ──
// Cosine-weighted hemisphere sampling, occlusion test against the scene.
// Returns ao in [0,1] where 1 = fully unoccluded.
void uml_embree_ao_bake(
    uml_embree_scene_t scene,
    const float* points, const float* normals, int point_count,
    int   samples_per_point,
    float max_distance,
    unsigned int rng_seed,
    float* out_ao);

// ── SDF grid (deferred to PR6) ──
void uml_embree_sdf_grid(
    uml_embree_scene_t scene,
    const float* origin, const float* cell_size,
    int nx, int ny, int nz,
    float* out_sdf);                            // signed distance per cell
```

All functions are thread-safe to call sequentially; Embree itself parallelizes
the batch internally via TBB. Do NOT wrap calls in Unity Job System — would
cause TBB/Job pool contention.

## Integration points

| File                            | Change                                                                                                          |
|---------------------------------|-----------------------------------------------------------------------------------------------------------------|
| `TriangleBvh.cs`                | Extract `IBvhBackend` interface (closest-point + raycast). Existing class implements it. No behaviour change.   |
| `TransferData.cs`               | Take `IBvhBackend` from factory instead of `new TriangleBvh(...)` directly.                                     |
| `GroupedShellTransfer.cs`       | Same pattern.                                                                                                   |
| `CoverageSplitSolver.cs`        | Same pattern.                                                                                                   |
| `VertexAOBaker.cs`              | Add `Backend.Embree` to enum. Auto-select if `UNITYMESHLAB_EMBREE` and CPU path requested and scene non-empty.  |
| `VertexAOBaker.Embree.cs`       | New file: builds Embree scene from input mesh + occluders, calls `uml_embree_ao_bake`, fills vertex AO array.   |
| `PostprocessorDefineManager.cs` | Add Embree binary detection alongside existing FBX exporter detection.                                          |

`UvTransferPipeline.cs` — no changes, already abstracted through `TransferData`.

## Performance & caching

- **BVH build cost**: 50–500 ms for typical game meshes. Cache per
  `(Mesh.GetInstanceID(), Mesh.vertexCount, Mesh.triangles.Length)` tuple in
  `EmbreeScene` static dictionary. Drop on `AssemblyReloadEvents`.
- **Threading**: Embree handles parallelism. C# side issues one P/Invoke per
  batch of N points; do NOT slice into Jobs.
- **Memory**: `rtcReleaseScene` on dispose. EditorWindow `OnDisable` must
  flush the cache for any scenes it owns.

## Risks & gotchas

- **TBB runtime dependency**: Embree links TBB dynamically by default.
  Either statically link TBB inside `umlab-embree.dll` (preferred, single
  artifact) or ship `tbb12.dll` / `libtbb.so.12` next to it. CMake decision
  in PR1.
- **CRT mismatch (Windows)**: Embree precompiled binaries use MSVC dynamic
  CRT. Build `umlab-embree.dll` with the same CRT, otherwise heap-cross-DLL
  crashes. Pin to `/MD` in CMake.
- **glibc baseline (Linux)**: Build on Ubuntu 20.04 image to keep glibc 2.31
  compatibility. Newer images produce binaries that fail on older Steam
  Runtime / CentOS targets.
- **macOS not supported**: `Native~/Embree/CMakeLists.txt` will
  `message(FATAL_ERROR)` on `APPLE` to prevent accidental broken builds.
  Mac users get the managed fallback automatically (define is not set).
- **License**: Embree (Apache 2.0) and TBB (Apache 2.0) both require
  attribution. Add `LICENSE-EMBREE.txt` and `LICENSE-TBB.txt` to
  `Native~/Embree/third_party/`. Mention in `CHANGELOG.md` on first release.
- **Plugin import settings**: `umlab-embree.dll` and `umlab-embree.so` need
  `.meta` files marking them as Editor-only x86_64. Pattern matches the
  existing `xatlas-unity` `.meta` files.

## Rollout (small PRs)

### PR1 — Native skeleton + CI
- Add `Native~/Embree/` with CMake, empty C ABI stubs, `build_embree.{bat,sh}`.
- GitHub Actions matrix: `windows-latest`, `ubuntu-20.04`. Build artifact,
  do NOT commit binaries (built in CI, attached to release).
- No C# changes. Verify compile only.
- Attribution files in place.

### PR2 — P/Invoke wrappers + smoke test
- `EmbreeNative.cs`, `EmbreeScene.cs`.
- One EditMode test: build Embree scene from a unit cube, query closest-point
  from `(2, 0, 0)`, expect hit at `(0.5, 0, 0)`.
- Test gated by `UNITYMESHLAB_EMBREE`; CI sets the define after building the
  binary. Test is a no-op on Mac and on PRs that don't touch native.

### PR3 — `IBvhBackend` refactor
- Extract interface, refactor existing callers (`TransferData`,
  `GroupedShellTransfer`, `CoverageSplitSolver`) to use factory.
- Pure refactor: no Embree implementation yet. Existing managed path is the
  only backend. Tests must pass unchanged.

### PR4 — Embree transfer backend
- Implement `EmbreeBvh : IBvhBackend`.
- Factory returns Embree if `UNITYMESHLAB_EMBREE` is set, else managed.
- Benchmark harness: 100k closest-point queries on a 200k-tri mesh.
  Numbers in PR description (managed vs Embree, CI machine).

### PR5 — Embree AO backend
- `VertexAOBaker.Embree.cs`. Hook into `Backend.Embree` enum.
- Benchmark: 50k vertices × 64 samples on a 100k-tri scene.
  Compare `.Cpu`, `.Gpu`, `.Embree`. Numbers in PR description.

### PR6 — SDF generator (separate effort)
- Standalone tool using `uml_embree_sdf_grid`.
- New `IUvTool` implementation in `Editor/Tools/SdfGeneratorTool.cs`.
- Out of scope for this plan beyond the C ABI hook.

### Future — macOS
- Add `macos-latest` to CI matrix.
- Build universal2 (arm64 + x86_64) Embree with `EMBREE_ISA_NEON2X=ON`.
- Drop `.dylib` to `Plugins/macOS/`.
- Extend `PostprocessorDefineManager` detect to cover macOS path.
- Requires Apple hardware for testing. Deferred until available.

## Acceptance per PR

Each PR must:
- Build cleanly on Windows and Linux CI.
- Not regress any existing test.
- Not change behaviour when `UNITYMESHLAB_EMBREE` is unset (verified by
  running tests with the define stripped).
- Include before/after benchmark numbers for PR4 and PR5.

## Open questions

1. Static vs dynamic TBB link — decide in PR1 after measuring resulting
   `umlab-embree.dll` size with each option.
2. Where to host prebuilt binaries: GitHub Releases attached to package
   tags, or a separate `umlab-embree-binaries` repo. Affects how end users
   install the package via UPM.
3. Should the Embree AO backend support multi-mesh scenes (occluders from
   neighbouring meshes) in PR5, or single-mesh only and defer multi-mesh to
   a follow-up? Current `.Gpu` baker is single-mesh; matching that is
   simplest.
