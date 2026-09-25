# Remesh & Bake (experimental)

Build a new static low-poly mesh, new UV0 atlas, and reproject source materials in
the **Remesh & Bake** tab of **Tools → Mesh Lab → Open Mesh Lab**. The original
meshes, materials, importers, scene objects and LODGroups are not replaced.

## Workflow

1. Select a static model root. **Source root** follows the selection: a LOD child
   resolves to its LODGroup, and selections without a MeshRenderer (lights,
   cameras) are ignored. An object dragged into the field holds until the
   selection changes. Active/enabled MeshRenderers under it are combined
   in root-local coordinates; LODGroups contribute LOD0 only, collision nodes are
   excluded. Skinned renderers are rejected. Every contributing submesh needs UV0
   and an opaque Standard (metallic) or URP/Lit material.
2. Set voxel resolution (4–256), target triangles and maximum geometric error.
   The triangle count is a target: the error limit can stop reduction earlier.
   Higher voxel resolution preserves smaller gaps but increases intermediate cost.
3. Choose atlas resolution, padding and projection distance (fraction of the
   source bounds diagonal). Generate & Bake runs native geometry processing and
   CPU BVH projection off the main thread. Texture snapshots and Unity mesh/asset
   APIs stay on the main thread.
4. Inspect the triangle count, color atlas and projection-miss report. Missed
   covered texels are magenta, not silently patched with unrelated material data.
   Increase projection distance or adjust remesh settings if needed.
5. Save to a folder under Assets. A unique output folder contains the mesh asset,
   material, prefab and BaseColor/Normal/MetallicSmoothness/Occlusion PNGs plus a
   linear floating-point Emission EXR. Source files are never overwritten.
   Instantiate the saved prefab to inspect the actual lighting and silhouette.

## Geometry implementation

`Native~/src/remesh.cpp` uses meshoptimizer v1.3
(`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`, 2026-09-25), pinned by full commit SHA.
The sequence is voxel remesh, position weld, simplifyWithUpdate with PreserveFolds
and RegularizeLight, crease-aware normal generation, then xatlas full unwrap.
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
- No skinning, blend-shape, custom shader, HDRP, alpha cutout/transparency,
  parallax or detail-layer transfer. Unsupported material modes fail explicitly.
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
flag that stops reaching meshoptimizer fails the build. CI runs it on all three
native platforms.

Unity Test Runner: `RemeshBakeTests` covers UV raster barycentrics, separate material
channels/HDR emission, source-normal to destination-tangent projection, image
sampling/wrapping, coincident-surface projection and cancellation. Run these in
Unity 6000.0+ after the native binaries are updated. The repository's Unity CI is
license-gated; a skipped job is not a passed compilation/test run.

Manual gate: textured multi-submesh prop, nested transforms including negative
scale, normal-mapped high-poly with bevels, thin sheet, LODGroup, 4K source texture,
missed projection, cancel/tab switch, and export/reimport in Built-in and URP.
Compare actual rendered output against the source and confirm the source assets
remain byte-for-byte unchanged.
