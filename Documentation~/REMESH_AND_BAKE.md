# Remesh & Bake (experimental)

Build a new static low-poly mesh, new UV0 atlas, and reproject source materials in
the **Remesh & Bake** tab of **Tools → Mesh Lab → Open Mesh Lab**. The original
meshes, materials, importers, scene objects and LODGroups are not replaced.

## Workflow

The tab runs four stages. Each stage has its own settings and button; running a
stage first brings every earlier stage up to date (missing output or changed
settings, marked "settings changed" in its header) and clears everything after it.
**Run all stages** re-runs the whole chain. The right panel previews the result:

- **3D** — orbitable view (drag to orbit, scroll to zoom) of the source, voxel
  remesh, simplified mesh or final result, with wireframe, shading, baked base
  color and vertex color toggles.
- **UV** — the final UV layout with islands tinted and the baked base color under
  it; island count, triangle count and texel usage.
- **Maps** — each baked map (base color, normal, metallic/smoothness, occlusion,
  emission).

1. **Source.** Select a static model root. **Source root** follows the
   selection: a LOD child resolves to its LODGroup, and selections without a
   MeshRenderer (lights, cameras) are ignored. An object dragged into the field
   holds until the selection changes. Active/enabled MeshRenderers under it are
   combined in root-local coordinates; LODGroups contribute LOD0 only, collision
   nodes are excluded. Skinned renderers are rejected. Every contributing submesh
   needs UV0. Read/Write-disabled imports are read from the imported asset.
2. **Voxel remesh** — voxel resolution (4–256), fit to source surface, two-sided
   shell. Higher resolution preserves smaller gaps but produces a denser,
   uniform intermediate mesh.
3. **Simplify** — quadric simplification of the voxel mesh. It collapses the
   cheapest edges first, so with *Regularize = None* flat areas reduce to a few
   large triangles while curved or detailed areas keep their density. *Maximum
   error* is relative to the mesh size. *Stop at triangles* ends simplification
   at that count or at the error limit, whichever comes first; 0 lets the error
   alone decide. *Light/Strong* regularization evens out triangle sizes instead.
   *Preserve folds* keeps sharp creases; *Remove small parts* drops tiny
   disconnected pieces. Turn *Simplify* off to unwrap the voxel mesh as is.
4. **Normals & UV** — *Hard edges*:
   - *Smooth* — no hard edges.
   - *Angle* — edges sharper than *Crease angle*.
   - *UV islands* — hard exactly along UV island borders, smooth inside each
     island (the usual choice for baked normal maps).
   - *UV islands + angle* — both.

   *Islands & packing* exposes the xatlas chart options: max cost (lower = more,
   smaller islands), normal deviation, hard-edge seam weight (islands prefer to
   break on hard edges), straightness, roundness, iterations, max island area and
   border length (source units, 0 = unlimited), rotation, 4×4 block alignment and
   brute-force packing. Texture size and padding set the atlas.
5. **Bake** — projection distance (fraction of the source bounds diagonal),
   *Samples per texel* (1, 4, 9 or 16; stratified supersampling that also covers
   texels only partly inside an island, for clean chart edges and less aliasing)
   and vertex color transfer: *Vertex color (RGB)* and *Vertex alpha* copy the
   source vertex colors, interpolated at the nearest source surface point, onto
   the result mesh independently. Missed covered texels are magenta, not silently
   patched with unrelated material data.
6. **Save** to a folder under Assets. A unique output folder contains the mesh
   asset (with transferred vertex colors), material, prefab and
   BaseColor/Normal/MetallicSmoothness/Occlusion PNGs plus a linear
   floating-point Emission EXR. Source files are never overwritten.

Native work (remesh, simplify, unwrap) and CPU projection run off the main thread.
Texture snapshots and Unity mesh/asset APIs stay on the main thread.

## Geometry implementation

`Native~/src/remesh.cpp` uses meshoptimizer v1.3
(`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`, 2026-09-25), pinned by full commit SHA.
Staged exports (ABI 2) run voxel remesh + position weld (`meshLabVoxelRemesh`),
simplifyWithUpdate with the selected regularize/fold/prune options plus degenerate
cleanup (`meshLabSimplify`), and crease-aware normal generation + xatlas unwrap
with explicit chart/pack options (`meshLabUnwrap`, which also returns each
vertex's island). The original one-shot `meshLabRemeshBuild` remains for the
native tests. "UV islands" hard edges are computed in C#: xatlas splits vertices
along island borders, so averaging face normals per output vertex smooths inside
islands and leaves their borders hard.
v1.3 removed the non-functional Thicken flag and renumbered `meshopt_RemeshShell`
and `meshopt_RemeshSolve`. The bridge keeps its own flag bits (1 = fit source
surface, 2 = two-sided shell) and maps them by name, so the C# ABI is unchanged;
the native test pins that mapping.

Each job owns its own xatlas instance and result handle, independently of the
legacy global repack bridge. C# always destroys the handle, including cancellation
and failure. Native exports use a version probe and capacity-checked copies.
The intermediate mesh is limited to five million triangles.

Native binaries must be rebuilt by **Build Native Libraries**. Its push job
publishes Windows x64, Linux x64 and universal macOS binaries into the branch;
its PR jobs compile/test without publishing. An old binary fails the capability
probe with an actionable message instead of invoking a missing entry point.
Existing bridge signatures and existing UV2 transfer behavior are unchanged.

## Material transfer

For every covered destination texel, the CPU traces from the destination surface
plus its normal offset back toward the source. A miss falls back to a bounded
nearest-point query. Barycentric source UV0 selects the original submesh material.
Normal maps are decoded on the GPU before readback and transformed from source
TBN to destination TBN. Metallic/smoothness, occlusion and emission stay separate
from base color. The output normal map is imported as a Unity normal map.

Material textures are read at their imported dimensions, without the web demo's
1K rescaling. The readback cache has a 512 MiB limit (HDR emission costs 16 bytes
per pixel). Source color maps retain sRGB byte precision and are interpolated in
linear space; normal/data maps use linear bytes. Emission readback/export uses
floating point. This does not recover detail already lost to source import
compression or max-size settings.

## Boundaries and current limitations

- Experimental triangle remeshing, not animation-ready quad retopology. Thin
  sheets, tiny gaps and adjacent disconnected parts can collapse or merge.
- No skinning, blend-shape, alpha cutout/transparency, parallax or detail-layer
  transfer. Shaders other than Standard and URP/Lit bake base color, normal,
  occlusion, emission and scalar metallic/smoothness from common property names;
  every such downgrade is logged as a warning. Result materials target Built-in
  and URP; HDRP export is not implemented.
- CPU projection is slower than a dedicated GPU baker, particularly at 4K.
  Source snapshot/readback and export are synchronous editor operations.
- Cancellation is observed after the current native remesh/unwrap completes,
  and throughout CPU projection. Assembly reload is deferred while the job owns
  native resources. Switching tabs requests cancellation and disposes previews.
- UV0 is newly generated; this feature does not generate/transfer lightmap UV2.
- Output color/normal/material maps use 8-bit PNG; emission is float EXR.
- Save creates assets, not a scene replacement. Asset export is not scene Undo;
  use the generated folder as the unit of deletion. A failed export rolls it back.
- Unity visual QA remains required before release: native and static checks alone
  do not establish shader, material or imported-normal-map correctness.

## Validation

Native tests require no Unity license:

```sh
cmake -S Native~ -B build -DCMAKE_BUILD_TYPE=Release -DMESHLAB_BUILD_TESTS=ON
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
```

The native test runs the complete cube → remesh → simplify → normal → UV pipeline,
checks finite data, normalized normals/UVs and valid indices, verifies the target
budget under relaxed error, repeated owned-handle cleanup and invalid input/copy
capacity rejection. It also runs every solve/shell flag combination and requires
the two-sided shell of the closed cube to be larger than the solid remesh, so a
flag that stops reaching meshoptimizer fails the build. The staged exports are
tested separately: voxel output copy and capacity guard, error-limited
simplification collapsing the flat cube faces far below the voxel density,
rejection of unknown flags and malformed unwrap options, and an unwrap whose
indices, island ids and UVs are all in range. CI runs it on all three native
platforms.

Unity Test Runner: `RemeshBakeTests` covers UV raster barycentrics, separate material
channels/HDR emission, source-normal to destination-tangent projection, image
sampling/wrapping, coincident-surface projection and cancellation, multisampled
coverage of a chart thinner than a texel, independent vertex color/alpha transfer,
UV-island hard edges and source-root selection following. Run these in
Unity 6000.0+ after the native binaries are updated. The repository's Unity CI is
license-gated; a skipped job is not a passed compilation/test run.

Manual gate: textured multi-submesh prop, nested transforms including negative
scale, normal-mapped high-poly with bevels, thin sheet, LODGroup, 4K source texture,
missed projection, cancel/tab switch, each hard-edge mode, every preview view, a
Read/Write-disabled FBX, a non-Standard shader, and export/reimport in Built-in and URP.
Compare actual rendered output against the source and confirm the source assets
remain byte-for-byte unchanged.
