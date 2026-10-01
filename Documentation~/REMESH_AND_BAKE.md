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
  color, baked normal map and vertex color toggles. The **Bump** toggle applies
  the baked tangent-space normal map to the result stage only (earlier stages
  have no tangents); with *Hard edges = UV islands* the vertex normals are
  smooth by design, so this toggle is what shows the transferred detail — the
  vivid pink/cyan atlas in **Maps ▸ Normal** is that same detail in tangent
  space, not corruption.
- **UV** — the final UV layout with islands tinted and the baked base color under
  it; island count, triangle count and texel usage.
- **Maps** — each baked map (base color, normal, metallic/smoothness, occlusion,
  emission).

1. **Source.** Select a model root. **Source root** follows the
   selection: a LOD child resolves to its LODGroup, and selections without a
   MeshRenderer (lights, cameras) are ignored. An object dragged into the field
   holds until the selection changes. Active/enabled MeshRenderers under it are
   combined in root-local coordinates; LODGroups contribute LOD0 only, collision
   nodes are excluded, and **LOD0 only** (default on) skips meshes named
   `Name_LOD1` and higher wherever they sit. **Shape** picks what the remesh
   stage builds from the capture: *LOD0* voxelizes the geometry (the default);
   *Bounding box* replaces every captured renderer with its own oriented box —
   measured over the renderer's geometry along the renderer's authored axes and
   placed back in the capture space, so a yawed building keeps a yawed box
   instead of the inflated axis-aligned one (weld: one box per renderer, all in
   one mesh; keep-hierarchy: one box per node; a flat renderer gets a minimal
   slab thickness); *Hull* runs the same geometry
   through a coarse voxel pass (*Hull resolution*, solid fill, no shell fit) and
   a strongly regularized simplification down to *Hull triangles*, giving a
   closed, rounded blob that follows L- and T-shapes without the box
   decomposition's guesswork. Both proxy shapes bake the original's materials
   and lighting through the proxy projection described under **Bake**.
   **Exclude parts smaller than** (a fraction of the capture diagonal) and
   **Exclude rods thinner than** (in voxel cells) run before any shape over the
   connected pieces of the capture — the connected components of the
   position-welded triangle graph, so a pipe goes even when it shares a mesh
   with the wall. A piece goes when its extent is under the size fraction, or
   when its cross-section — the two smaller of its three extents along its own
   principal axes — is under the rod threshold, a cell being the model's
   longest side divided by the voxel (or hull) resolution: that is the section
   the grid cannot carry anyway. Thin is not the criterion: a gate leaf, a
   glass pane or a decal has one thin extent and stays; pipes, cables, railings
   and bolts have two and go. The stage status reports how many pieces went,
   and a filter that would remove everything is skipped with a warning instead
   of producing an empty remesh. **Highlight capture in Scene** paints the
   source in the Scene view as the remesh stage will see it, before any stage
   runs and live with the filter sliders: green = captured, orange = dropped
   as a small part, red = dropped as a rod, grey = renderer the capture skips
   (LOD1+, collision, disabled). It is a geometry-only capture (no material or
   texture reads) drawn over the originals with a depth offset; the sidebar
   shows the triangle counts per class.
   SkinnedMeshRenderers are baked at their
   current pose (skinning re-evaluated first — in edit mode it can be stale
   and bake every part at its authored origin) and then captured like static
   meshes; pose the model the way you want it baked. Every contributing submesh
   needs UV0. Read/Write-disabled imports are read through MeshData; the source
   importers are never touched (no reimports).
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
4. **Normals & UV** — *Hard edges* (defaults to *UV islands*):
   - *Smooth* — no hard edges.
   - *Angle* — edges sharper than *Crease angle*. On coarse organic
     decimations most edges exceed the crease, which reads as fully faceted
     and also feeds xatlas the crease-split normals as seams, shattering the
     atlas into slivers — prefer *UV islands* for baked results.
   - *UV islands* — hard exactly along UV island borders, smooth inside each
     island (the usual choice for baked normal maps).
   - *UV islands + angle* — both.

   Whichever mode is active, the vertex normals and tangents are regenerated
   **after** the UV cut: normals accumulate weighted face normals over the split
   geometry (smooth inside every split group, hard across creases and island
   borders alike), and tangents come from meshoptimizer's MikkT-compatible
   generator over the final atlas layout — one basis for the bake, the preview
   and the saved mesh. *Normal weighting* selects the accumulation weight
   (Blender Weighted Normal analog): face area, corner angle, or both.

   *Normal smoothing* (0–10) is applied **after** UV generation, so it behaves
   the same whichever hard-edge mode is active: smoothing flows along the
   surface through mesh edges and stops at every hard edge — crease splits and
   island borders alike (edges never cross a vertex split). With it applied
   before the unwrap, the island hard-edge rebuild discarded it, leaving the
   slider dead in the default mode.

   Stage settings persist across domain reloads and tab switches (EditorPrefs);
   *Run all stages* still re-runs everything with the restored values.

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
   patched with unrelated material data. Projection rays are cast along a smooth
   welded "cage" direction (area-weighted face normals averaged across coincident
   vertices), not the vertex normal: UV-island hard edges leave chart-border
   normals one-sided, and rays along them would sample a displaced source point,
   baking artifact bands around every island. The tangent-space normal map is
   still encoded against the vertex normal the result mesh shades with, so hard
   island borders keep their crisp silhouette while the interior stays clean.
   Rays only accept source triangles facing them (front-face filter): a plain
   closest-hit ray travels twice the projection distance through the target and
   pierces thin walls, sampling the far side's texture as periodic mirrored
   patches; the filter's orientation comes from a probe that looks at the
   source from OUTSIDE — rays cast from a sphere around it toward its centre
   meet an outer surface first, and that triangle's winding against the ray
   gives the answer whatever the target's density — and it stays off when
   fewer than 70% of the rays agree (open sheets, mixed winding). The 3D preview's **Cage** toggle
   draws the projection limits — the result mesh inflated by ±the ray travel
   along the same welded cage normals, orange for the outer (ray origin) shell,
   blue for the inner (ray end) shell — live from the current projection
   distance, so the setting can be tuned before re-baking. The cage is a proper
   smooth cage: its directions are area-weighted, welded across UV/crease splits
   and Laplacian-smoothed over the welded connectivity, and each shell's offset
   stops short of self-intersection (cast against the surface itself), so a
   tight concavity shows a pinch instead of folding through to the far side.
   For the *Bounding box* and *Hull* shapes the cage is replaced by **proxy
   projection**: every texel casts straight along its face normal from just
   outside the proxy through the proxy's full depth and takes the first source
   surface it meets, so each proxy face shows what a viewer looking at that face
   would see; there is no nearest-surface fallback. Texels whose ray meets no
   geometry (the empty corners of a box around an L-shaped building) are written
   with **alpha 0** in the color map and filled from the nearest hit texel, and
   the stage status counts them — a shader that clips on alpha turns the proxy
   into a silhouette-correct impostor. **Vertex color tints albedo** multiplies
   the baked albedo by the source's interpolated vertex color (RGB, read as
   linear, the way vertex-tinting shaders do), independently of the vertex color
   transfer toggles. It is on by default and does nothing on meshes without
   vertex colors; turn it off for shaders that ignore the vertex color.
   **Bake mode** selects what lands in the maps: *Materials* transfers the source
   maps; *Beauty* bakes the object as the player sees it — realtime/mixed light
   with hard ray shadows, the renderer's lightmaps (sampled at its UV2, RGBM/HDR
   decoded), ambient (flat, trilight or the ambient probe) and reflection probes
   (equirectangular readbacks, roughness-lerped) all folded into one lit
   **BaseColor** texture. Baked-only lights are skipped for lightmapped faces, so
   nothing is counted twice; unlightmapped faces get albedo × (direct + ambient) +
   emission, lightmapped faces get albedo × (lightmap + realtime direct) +
   emission — Unity lightmaps store irradiance, which the Lit shader multiplies
   by the material's albedo at runtime, so the bake does the same. Specular is
   view-dependent and is baked for the scene view camera's position at bake time.
   Scene lighting is captured once in world space and every capture space (the
   weld, or each keep-hierarchy node) converts into it, so light ranges, shadow
   distances and probe bounds stay in world units whatever the source's scale.
   Lightmaps are read only for a Beauty bake, right before it runs, and only
   the region each renderer occupies (its lightmap scale/offset rect, one texel
   of padding); a region above 2048² texels is downsampled to fit, and the
   float readbacks are released when the bake ends — the capture itself keeps
   just the texture references, so Materials bakes never touch the lightmaps.
   The saved material becomes **Unlit/Texture** with that one map (the other maps
   still export alongside); the 3D preview renders beauty results unlit, exactly
   like the saved material.
   Bakery setups work through the same path: its HDR colour map decodes directly
   (8-bit plain output is auto-detected by its pinned alpha and skips Unity's RGBM
   decode), the separate direction texture is not needed (the colour already is
   that surface's irradiance), and shadowmask lights contribute through their
   realtime component with the bake's own ray shadows standing in for the mask.
   In directional mode the bake reproduces the game's exact lightmap response —
   URP's `SampleDirectionalLightmap`: the encoded dominant direction is dotted
   with the surface's world normal as a half-Lambert and divided by the texel's
   rebalancing coefficient — not a flat colour, so oblique surfaces shade the way
   they do in play. Reflections follow the game's specular path too — URP's
   box-projected probe direction, the prefiltered probe mips selected by the
   r(1.7−0.7r)·maxMip remap, and `EnvironmentBRDFSpecular` (surface reduction,
   grazing term, Schlick Fresnel) — and all lighting, lightmap response and
   specular alike, reacts to the source's normal map, as it does in play.
   With the **RemeshDiag** log filter enabled, every bake also prints its health
   counters to the Console: welded cage positions and split copies, one-sided
   border normals (max cage deviation), nearest-fallback projection samples,
   front-face filter state and the normal map's tilt statistics (mean/max angle
   from flat, texels >45°); a map dominated by extreme tilts additionally raises
   a warning.
6. **Save**. The output lands in a `remesh` subfolder next to the source model's
   asset (its FBX/prefab, or any mesh asset under it), created if missing;
   scene-only sources fall back to a folder picker. A unique output folder
   inside it contains the model, material, prefab and
   BaseColor/Normal/MetallicSmoothness/Occlusion PNGs plus a linear
   floating-point Emission EXR. Source files are never overwritten.
   *Normalize size (saved at scale 1)* (default on) bakes the source root's
   world scale into the saved geometry — real size, identity transform — with
   normals and tangents taking the inverse scale and the winding flipping on a
   mirroring determinant; off keeps root-local geometry and carries the scale
   on the saved transform instead. The RemeshDiag bake summary also reports
   the source/target bounds-diagonal ratio and warns when it drifts from 1.
   - With **com.unity.formats.fbx** installed the model is exported as a binary
     **FBX** (mesh `Name_LOD0` with the generated normals — including UV-island
     hard edges — UV0, tangents and transferred vertex colors), a prefab that
     instantiates the FBX with the curated material assigned, and the `.mat` next
     to it. The FBX importer is set to not generate its own material, so the
     folder stays curated. *Embed textures in FBX* (default on) embeds the baked
     maps into the binary FBX, making it self-contained and portable; turned off,
     the FBX links the exported maps by absolute path on the exporting machine.
     Mind the size: the float EXR emission map alone adds 16 bytes per texel.
   - Without the FBX package the mesh is saved as a Unity `.asset` plus the same
     material, prefab and maps.

Native work (remesh, simplify, unwrap) and CPU projection run off the main thread.
Texture snapshots and Unity mesh/asset APIs stay on the main thread.

## Code layout

- `Editor/Tools/RemeshBakeTool.cs` — the tab: source selection, per-stage
  settings and buttons, the right-sidebar previews, the save button.
- `Editor/RemeshPipeline.cs` — the stage machine. The work unit is a node
  (capture → voxel → simplified → unwrapped → baked); the weld is one node in
  root space, keep-hierarchy one node per renderer, and every stage runs the same
  loop. Owns every mesh and preview texture it creates; `ClearFrom(stage)` drops
  a stage and everything after it; `Key`/`IsStale` drive the "(settings
  changed)" markers.
- `Editor/RemeshSource.cs` — `CollectRenderers` (the one filter for both lanes),
  `Capture` (geometry, materials, textures and lightmap references in a given
  space; `ReadLightmaps`/`ReleaseLightmaps` around a Beauty bake).
- `Editor/RemeshNative.cs` — the P/Invoke bridge and the post-unwrap normal /
  tangent regeneration.
- `Editor/RemeshBaker.cs` — the CPU projection; `Editor/RemeshBeauty.cs` — the
  scene lighting snapshot for Beauty bakes.
- `Editor/RemeshExporter.cs` — the save: maps, materials, meshes, prefab or FBX,
  one path for the weld and for hierarchy nodes.
- `Editor/RemeshPreview.cs` — the 3D / UV / Maps previews.

## Geometry implementation

`Native~/src/remesh.cpp` uses meshoptimizer v1.3
(`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`, 2026-09-25), pinned by full commit SHA.
Staged exports (ABI 3) run voxel remesh + position weld (`meshLabVoxelRemesh`),
simplifyWithUpdate with the selected regularize/fold/prune options plus degenerate
cleanup (`meshLabSimplify`), and crease-aware normal generation + xatlas unwrap
with explicit chart/pack options (`meshLabUnwrap`, which also returns each
vertex's island). The original one-shot `meshLabRemeshBuild` remains for the
native tests. The unwrap vertex layout is sixteen float32 values — position,
normal, UV0, tangent (xyz direction plus ±1 handedness) — with the tangent
regenerated by `meshopt_generateTangents` (MikkT-compatible) from the final
atlas layout, so the bake, the preview and the saved mesh all encode against
one basis; corners that disagree duplicate their vertex.
The vertex normals themselves are regenerated in C# **after** the UV cut from
the split geometry. In the UV-island modes every output vertex is its own
normal group: crease splits and chart borders are already vertex splits and no
face crosses one, so the accumulation smooths inside every island and stays
hard across creases and island borders alike. In *Smooth* and *Angle* the
copies xatlas duplicated along chart borders are grouped back together by
position and native (pre-chart, crease-split) normal, so islands stay smooth
and only the crease edges harden; normal smoothing runs over the same groups.
The *Normal weighting* option selects the accumulation weight — face area
(meshopt's own), corner angle, or both multiplied (the Blender Weighted
Normal modifier analog). Tangents are re-orthogonalized against the final
normals so the saved frame matches the one the bake encodes against.
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
- **Keep hierarchy** (`RemeshSettings.keepHierarchy`) splits the capture at the
  renderer level: every node the weld would have folded in runs the whole
  voxel → simplify → unwrap → bake chain in its own local space, and the save
  rebuilds them as children of one root prefab at their captured root-relative
  transforms, each with its own baked material and mesh asset. The FBX lane
  stays weld-only (a multi-node FBX round-trip re-imports per-node normals and
  mappings for no gain over the native prefab), the source root's scale stays on
  the prefab root (so *Normalized size* is weld-only and says so), and preview
  panels show the largest node's stage outputs. Node granularity = the renderers
  of the capture scope (the same list the weld captures); each renderer is its
  own node, so nothing is captured twice. A node's capture space is the TRS the
  save restores, so a sheared renderer matrix (rotated child under a
  non-uniform parent) folds its residual into the captured geometry.
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
