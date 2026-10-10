# Remesh & Bake (experimental)

Build a new static low-poly mesh, new UV0 atlas, and reproject source materials in
the **Remesh & Bake** tab of **Tools → Mesh Lab → Open Mesh Lab**. The original
meshes, materials, importers, scene objects and LODGroups are not replaced.

Source-preview mesh reads and skinned posing are scheduled after IMGUI drawing.
The sidebar shows `Preparing source preview…` while queued. Empty meshes are
omitted from the preview. If a nonempty mesh has no Position channel or its data
cannot be read, the partial preview is discarded and the sidebar shows the error
with **Retry source preview**. Selecting another source or refreshing also retries.
Queued work is cancelled when the preview/tool is cleared. MeshData availability
is checked against the source layout/count; imported buffers use the Editor
MeshUtility read-only snapshot without changing Read/Write. Unavailable snapshots
retain the temporary importer fallback outside IMGUI, while truly empty copies do
not acquire vertex data or reimport anything. These checks affect preview handling,
not the strict geometry validation of Remesh/Reverse UV stages.

## Workflow

The tab prepares the source and then runs four native/bake stages. **Prepare / inspect Cap & Bridge**
in the Remesh section captures and filters the source, welds the geometry-only
support and prepares the selected closures without invoking native Remesh. The
**Cap / Bridge** preview shows all retained hierarchy nodes: grey original faces,
orange/purple closure patches, cyan original hole rims and red refused contours.
Each refused contour has a selectable reason; its refusal does not cancel closures
on other contours. A failed compound Cap or Bridge is rolled back in full. Its summary reports
initial loops, generated patches/faces and remaining boundary edges. A partial
loop selection can be inspected even with other holes still open. Remesh consumes
the same snapshot; a subsequent solid-input or native-output refusal retains it.
Changing capture/filter/closure settings requires preparation again. Changing
only Solve, trim, Simplify, UV or bake settings reuses the captured support.

Closure contacts are checked against its connected geometric element after weld.
Faces and earlier Caps from other elements do not block closure, including inside
one Mesh asset. Bridges protect both elements they join; subsequent closures use
the updated connectivity. Unsupported contours still retain their refusal reasons.

Automatic closure supports more than 16 selected contours, with work budgets for
partner analysis instead of a global loop-count refusal. It recognizes mutually
continuing collars and close, oppositely wound thin skins as Bridge candidates.
Independent rims try local planar closure and then an audited curved Surface Cap.
The full proposal is discarded on refusal; accepted closures on other rims remain.

Each stage has its own settings and button; running a
stage first brings every earlier stage up to date (missing output or changed
settings, marked "settings changed" in its header) and clears everything after it.
**Run all stages** re-runs the whole chain. The right panel previews the result:

- **3D** — a vertical list for the narrow right column: the **Stage** (source,
  voxel remesh, simplified mesh or final result) shown in the canvas's 3D view,
  then what is specific to this tool — **Baked base color** and **Baked normal
  map** on the result, **Trim mask** on the remesh stage, **Cage shells** on the
  result. Wireframe, the shading modes (vertex colours, normals, tangents, UV
  channels), the UV fill mode, the island borders (**Bdr**) and spot picking are
  the canvas's own controls (status bar and the 3D view's shading row) and
  apply to the stage mesh like to any other mesh: with the result stage shown,
  the UV layer draws the atlas's islands and their borders on the model in 3D.
  The normal-map toggle applies the baked tangent-space normal map to the
  result stage only (earlier stages have no tangents); with *Hard edges = UV
  islands* the vertex normals are smooth by design, so this toggle is what
  shows the transferred detail — the vivid pink/cyan atlas in **Maps ▸ Normal**
  is that same detail in tangent space, not corruption.
  **Trim mask**, at the *Remesh* stage, shows the untrimmed remesh coloured
  by what *Trim to source surface* did with each face: green kept, red the back
  of a sheet (a source face within reach faces the other way), orange a rim or a
  face with no source within reach — so what the cut removes, and why, is
  visible before the simplifier touches it.
  **Part filter highlight**, at the *Source* stage, paints the original capture
  green for kept parts, orange for parts removed by size, and red for parts
  removed by thickness. It follows the filter sliders and selected root, and
  includes completely filtered nodes in *Keep hierarchy*. The paint uses
  temporary preview meshes and does not replace scene meshes or materials.
  The 3D panel also reports the atlas: island count and texel usage.
- **Maps** — each baked map (base color, normal, metallic/smoothness, occlusion,
  emission).

The canvas's **UV** mode (the UV | 3D switch at its bottom) shows the result's
atlas once Normals & UV ran — the same shells, wire, border, spot picking and
status line as any mesh — over the baked base color (or the checker when the
canvas asks for it), with an **Islands** fill mode that tints every UV shell.
Before the UV stage the canvas shows the selected model as usual.

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
2. **Voxel remesh** — voxel resolution (4–1024), fit to source surface, two-sided
   shell. Higher resolution preserves smaller gaps but produces a denser,
   uniform intermediate mesh. **Trim to source surface** (default on) masks the
   result against the source: the voxelizer closes every surface, so an open
   sheet (a wall, a roof plane, a curtain) comes back as a slab with a front, a
   back and rims — and because the remesher fits its vertices onto the input
   surface, the front and the back lie ON the sheet, a zero-thickness
   double-sided surface rather than a cell-thick slab. A remesh face stays only
   when a source face lies within two voxel cells of it whose normal points the
   same way; the back of a one-sided sheet (opposite normal) and the slab's
   rims (perpendicular) have none and go, a closed source is left whole, and a
   source that is double-sided where it matters keeps both sides. A source
   wound inside out is judged by its flipped normals; when neither reading keeps
   a tenth of the remesh, nothing is trimmed and a warning says so. The kept
   sheet is then re-wound to one consistent orientation per connected piece
   (neighbours must traverse their shared edge in opposite directions; the
   majority of each piece keeps the side that agreed with the source), so a
   source modeled with arbitrary winding — common under a two-sided material —
   comes out orientable. The status reports how many faces went and how many
   were re-wound, and the Remesh stage's **Trim** toggle colours the untrimmed
   remesh by class. Turn it off to keep the slab, for instance with *Two-sided
   shell*. **Source backfaces** decides which source faces count from behind as
   well: *From materials* (default) marks a material two-sided when its cull
   mode property (`_Cull`, `_CullMode`) is Off or a double-sided switch
   (`_DoubleSidedEnable`, `_TwoSided`, `_DoubleSided`) is on; *Always* treats
   every face as two-sided (for a `Cull Off` written into the shader itself,
   which no property reveals); *Never* keeps only the front of every face. A
   two-sided source still yields ONE sheet — the result material renders both
   sides of it instead (URP Lit: Render Face Both; Standard has no two-sided
   mode, and the save says so) — the bake's front-face filter accepts such a
   face from either side, and the winding probe ignores it. The setting marks
   both the remesh and the bake stage stale.
3. **Simplify** — quadric simplification of the voxel mesh. It collapses the
   cheapest edges first, so with *Regularize = None* flat areas reduce to a few
   large triangles while curved or detailed areas keep their density. *Maximum
   error* is relative to the mesh size. *Stop at triangles* ends simplification
   at that count or at the error limit, whichever comes first; 0 lets the error
   alone decide. *Light/Strong* regularization evens out triangle sizes instead.
   *Preserve folds* keeps sharp creases; *Remove small parts* drops tiny
   disconnected pieces. Turn *Simplify* off to unwrap the voxel mesh as is.
   Generated intermediate geometry (voxel, trim, boxes and simplify) always
   carries averaged, area-weighted normals. Missing UV0 is filled with local
   X/Y normalized independently to 0–1 (a flat axis maps to 0.5). These planar
   UVs are temporary channel data; the unwrap stage replaces them with the
   real atlas before UV-dependent normal modes or baking run.
   They carry a `draftUv` flag in stage geometry, exposed by the preview's
   `MeshEntry.draftUv` and shown as **Draft UV0**. Generated planar UV0 on a Unity
   mesh uses a reserved third component (`Z = -1048576`) for this provenance;
   XY stays unchanged, and the marker survives asset saving and domain reload.
   Authored UV channels are preserved. A completed xatlas unwrap replaces the
   draft UV0 with the real two-component atlas and clears this flag. Final normals and bake reject
   draft target UVs; `UvTopology.HasFinalUv` distinguishes them from usable
   channel data without hiding them from the UV preview.
4. **Normals & UV** — *Hard edges* (defaults to *UV islands*):
   - *Smooth* — no hard edges.
   - *Angle* — edges sharper than *Crease angle*. On coarse organic
     decimations a low crease angle can make the final shading faceted.
   - *UV islands* — hard exactly along UV island borders, smooth inside each
     island (the usual choice for baked normal maps).
   - *UV islands + angle* — both.

   Whichever mode is active, xatlas receives averaged normals. The final
   *Hard edges*, *Normal weighting* and *Normal smoothing* settings run
   **after** the atlas is selected. Angle modes then split vertices into
   normal fans without moving any triangle corner or changing its UV or chart.
   Normals accumulate weighted face normals inside the selected groups;
   tangents come from meshoptimizer's MikkT-compatible generator over the atlas
   and are re-orthogonalized against the final normals — one basis for the bake,
   the preview and the saved mesh. *Normal weighting* selects the accumulation weight
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
   The measured balanced chart default uses max cost 2, normal deviation 2,
   **roundness 0.5**, straightness 6, hard-edge seam 4 and one iteration.
   Rotation is on; block alignment and brute force are off. Previously saved
   values stay intact. **Recommended xatlas settings** explicitly applies these
   chart/packing defaults while preserving texture size/padding, optimizer
   toggles, mesh, shading and bake settings. See the
   [settings comparison](XATLAS_DEFAULTS_BENCHMARK.md) for the corpus and tradeoffs.
   **Reduce UV fragmentation** is enabled by default, including restored settings.
   It compares the requested unwrap with up to two chart-growth alternatives
   (max cost 5, normal deviation 2, roundness 0.01, normal seam 4, one iteration,
   straightness 6 or 10). It accepts a result only if neither the total island
   count nor the number of small islands (up to eight triangles) increases and
   at least one decreases. Area-weighted mean conformal stretch must stay below
   the greater of 1.15 or 110% of the original value; worst stretch must stay
   below the greater of 4 or 110% of the original value. Collapsed or flipped
   triangles within a chart reject the alternative. A normal seam weight of
   1000 (an explicit seam constraint) remains unchanged. Explicit island
   area/border limits, packing and mesh geometry are preserved. Final normal
   creases do not add chart seams. The work
   runs on the unwrap worker, with cancellation between native passes; the
   status reports before/after island counts. Turn it off to use only the
   manually configured chart settings. Automatic charting still chooses its
   own seams; this control does not specify anatomical seams on a head or suit.
   **Merge charts** (off by default) runs after the fragmentation search and
   deterministically merges adjacent island pairs: a Procrustes similarity
   (rotation + uniform scale + translation, no mirror) fits one chart's UVs onto
   its neighbour through the seam vertices, the seam is snapped bit-exact onto
   the acceptor's UVs, and the merge is accepted only within bounded seam
   residual (≤ 0.02 of the acceptor's UV size), texel-density change (≤ 2×),
   the island area/border limits, a full UV-triangle overlap test (contact along
   the new seam is allowed, and the moved chart is also checked against itself
   after the snap) and the same stretch bounds the fragmentation search uses.
   Before repacking merged charts through xatlas, a bounded distortion relax
   runs on the resulting UV islands. Relax moves
   free UV vertices while keeping joined seam copies exactly colocal, preserving
   each island's UV area, and accepting only non-increasing mean/worst stretch
   with a complete zero-overlap scan. Existing cuts and unsupported topology are
   skipped. It runs only after an accepted merge; the xatlas charting settings
   and unmerged unwrap stay as configured. The
   [relax comparison](UV_MERGE_RELAX_BENCHMARK.md) includes UV distortion heatmaps.
   Tangent frames are rebuilt
   from the final atlas layout — covering the fit rotation, the seam snap's
   per-vertex displacement and the packer's per-axis stretch alike — and any
   anomaly reverts to the unmerged unwrap. Adds one xatlas pack pass on the
   worker.
   Baking first fills that padding, then applies **Dilation radius (px)**
   (default 64, 0 disables the extra pass). Dilation extends all maps into the
   remaining background from the nearest filled pixel within the additional
   radius, without changing island pixels or averaging normal-map directions.
   It also applies to Texture AO and is included in previews and saved maps.
5. **Bake** — **Bake filtered-out parts** (default off) projects from the full
   captured source before the small-part/rod filter. Small bolts, pipes and
   cables can contribute materials, normals, vertex colours and lighting to
   the retained shape even though they were removed from Remesh. In *Keep
   hierarchy*, this includes fully filtered renderers under the source root;
   donors are rebased into each target node's space. The captured texture
   readbacks are reused. Changing this option requires only another Bake,
   and the cage preview uses the selected donor. Projection reach and backface
   rules still apply; the option does not restore removed geometry to the shape.
   **GPU projection** (default on where compute shaders exist) runs
   the geometry queries — the projection rays and the nearest-point fallbacks —
   on the GPU through `Shaders/BvhQueries.compute`, the same BVH as the CPU with
   the same filters and the same hit records, so the result is the one the CPU
   path produces, usually several times faster on large atlases; the atlas is
   processed in bands of rows (sample requests built on workers, answered in one
   dispatch per batch, evaluated on workers), and the status says which path
   ran. Projection distance (fraction of the source bounds diagonal),
   *Samples per texel* (1, 4, 9 or 16; stratified supersampling that also covers
   texels only partly inside an island, for clean chart edges and less aliasing)
   and vertex color transfer: *Vertex color (RGB)* and *Vertex alpha* copy the
   source vertex colors, interpolated at the nearest source surface point, onto
   the result mesh independently. Missed covered texels are magenta, not silently
   patched with unrelated material data. Projection rays are cast along a smooth
   welded "cage" direction, not the vertex normal: UV-island hard edges leave
   chart-border normals one-sided, and rays along them would sample a displaced
   source point, baking artifact bands around every island. The tangent-space
   normal map is still encoded against the vertex normal the result mesh shades
   with, so hard island borders keep their crisp silhouette while the interior
   stays clean. The cage is built **per face corner and per side**: the corners
   meeting at one position are clustered by the hemisphere their face normals
   share (within 120°), and only one cluster's corners are averaged (face area ×
   corner angle) and Laplacian-smoothed together over the cluster connectivity
   (**Cage smoothing**, 0–10 passes, 2 by default). UV-chart and crease splits
   weld back into one smooth direction as a plain cage does, but a double-sided
   sheet — the wall of a non-closed source, thinner than a voxel cell and
   collapsed to zero thickness by the simplifier, both windings on the same
   vertices — keeps a front side and a back side, where a position weld would
   sum two opposite normals to nothing and normalize the noise (rays leaving at
   180° from their face, preview shells spiking across the whole model). Every
   corner direction is checked against its own face and falls back to the
   unsmoothed side, then to the face normal, so no ray starts behind the surface
   it belongs to. **Fit cage to source** (default on) replaces the one global
   ray travel with a per-side reach: each side casts along its direction both
   ways (then asks for the nearest point) to measure where the source actually
   is, doubles that for oblique surfaces, clamps it between 1× and 8× the
   projection distance and smooths it over the side connectivity, never below a
   side's own need — a cage that hugs the source where the decimation stayed
   close and opens where it drifted, so fewer texels miss without deep rays
   everywhere. Rays only accept source triangles facing them (front-face
   filter): a plain closest-hit ray travels twice the reach through the target
   and pierces thin walls, sampling the far side's texture as periodic mirrored
   patches; the filter's orientation comes from a probe that looks at the
   source from OUTSIDE — rays cast from a sphere around it toward its centre
   meet an outer surface first, and that triangle's winding against the ray
   gives the answer whatever the target's density — and it stays off when
   fewer than 70% of the rays agree (open sheets, mixed winding). Two-sided
   source faces (see *Source backfaces*) pass the filter from either side and
   cast no vote in the probe. The 3D view's
   **Cage** toggle (the right sidebar's 3D panel drives the canvas's shared 3D
   view — switch the canvas to **3D** at its bottom centre) draws the projection
   limits — every corner pushed ±its reach along its cage direction, one line
   per welded side pair, orange for the outer (ray origin) shell, blue for the
   inner (ray end) shell — live from the current distance, smoothing and fit
   (the fit builds a BVH of the source once per capture), so the settings can be
   tuned before re-baking; a double-sided sheet shows both of its shells. Each
   shell's offset stops short of self-intersection (cast against the surface
   itself), so a tight concavity shows a pinch instead of folding through to the
   far side.
   For the *Bounding box* and *Hull* shapes the cage is replaced by **proxy
   projection**: every texel casts straight along its face normal from just
   outside the proxy, as deep as **Proxy search depth** allows (a fraction of
   the capture diagonal, 0.1 by default), and takes the first source surface it
   meets, so each proxy face shows what sits behind it; a ray that meets
   nothing falls back to the nearest surface within the same reach, so a hull's
   rounded corner still picks the wall beside it. Keep the depth short: a deep
   look sees across courtyards through the block and costs a whole BVH
   traversal per empty texel. Texels that find no geometry within reach (the
   empty corners of a box around an L-shaped building) are written
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
   Shadow rays test the source's own geometry and the rest of the scene's shadow
   casters (enabled, LOD0, casting shadows — captured geometry-only, nearest
   renderers first up to a 4M-triangle budget), so a neighbouring building, a
   canopy or a sibling keep-hierarchy node shadows the source as in the game;
   the Console lists what the snapshot held. Lights honour their culling mask
   per captured renderer layer: a light that does not reach the object's layer
   neither lights nor shadows it. Reflection probes are blended the way the
   runtime does — weight 1 inside a probe's box, falling to 0 across its blend
   distance, the two highest-importance probes sharing the sample and the
   environment reflection (the custom cubemap, or the reflection Unity
   generated from the skybox) taking the rest.
   Lightmaps are read only for a Beauty bake, right before it runs, and only
   the region each renderer occupies (its lightmap scale/offset rect, one texel
   of padding); a region above 2048² texels is downsampled to fit, and the
   float readbacks are released when the bake ends — the capture itself keeps
   just the texture references, so Materials bakes never touch the lightmaps.
   The saved material becomes the pipeline's unlit shader (**Unlit/Texture** in
   Built-in, **Universal Render Pipeline/Unlit** in URP) with that one map (the other maps
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
   counters to the Console: welded cage positions and split copies, cage sides
   and double-sided positions, vertices whose normal sits >30° off their cage
   (max deviation), the longest fitted reach as a multiple of the projection
   distance, nearest-fallback projection samples,
   front-face filter state, two-sided source face count and the normal map's tilt statistics (mean/max angle
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
- `Editor/Geometry/MeshGeometry.cs` — the shared geometry routines every
  projecting or baking tool uses (face normals, bit-exact position welding,
  Fibonacci sample directions, 2D barycentrics, point–box distance);
  `Editor/Geometry/GpuReadback.cs` — the one GPU → CPU texture readback (blit
  through an optional material or sub-rectangle into a linear temporary, then
  ReadPixels). Spatial queries are `Editor/TriangleBvh.cs` (3D: binned-SAH build,
  watertight two-sided ray test, nearest point, normal- and facing-filtered,
  either-side masks) and `Editor/TriangleBvh2D.cs`
  (UV space); `Editor/Geometry/GpuBvh.cs` is the same tree on the GPU with the
  same queries in batches (`Shaders/BvhQueries.compute`), and
  `Shaders/BvhTraversal.hlsl` is the one traversal every compute kernel includes
  (the vertex-AO kernel binds the tree through `GpuBvh.Bind`). No tool carries
  its own copy of any of these.

## Geometry implementation

`Native~/src/remesh.cpp` uses meshoptimizer v1.3
(`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`, 2026-09-25), pinned by full commit SHA.
The reviewed copy of its remesher in `Native~/third_party/meshoptimizer/` keeps
byte grid offsets for resolutions up to 256 and uses 16-bit offsets from 257 to
1024. The default remains 128. The grid alone uses 256 MiB at 512 and 2 GiB at
1024; surface data and intermediate mesh buffers add to that. Resolution means
cells along the longest axis, and the five-million-triangle output budget still
applies. Native libraries must come from the completed `build-native.yml` run.
Staged exports (ABI 3) run voxel remesh + position weld (`meshLabVoxelRemesh`),
simplifyWithUpdate with the selected regularize/fold/prune options plus degenerate
cleanup (`meshLabSimplify`), and averaged normal generation + xatlas unwrap
with explicit chart/pack options (`meshLabUnwrap`, which also returns each
vertex's island). The original one-shot `meshLabRemeshBuild` remains for the
native tests. The unwrap vertex layout is sixteen float32 values — position,
normal, UV0, tangent (xyz direction plus ±1 handedness) — with the tangent
regenerated by `meshopt_generateTangents` (MikkT-compatible) from the final
atlas layout, so the bake, the preview and the saved mesh all encode against
one basis; corners that disagree duplicate their vertex.
Every generated intermediate mesh receives averaged normals and missing UV0
gets normalized local XY. The native unwrap runs with crease PI and smoothing
zero, independent of the final normal settings. `Editor/Geometry/RemeshNormals.cs`
applies those settings in C# **after** UV generation and candidate selection.
*Smooth* groups chart-border copies by position and native averaged normal;
*UV islands* accumulates within the native vertex splits. Angle modes build
connected corner fans across manifold edges below the crease threshold, with
the combined mode also stopping at chart borders. Render vertices shared by
different fans are duplicated with their position, UV, chart and tangent
handedness preserved. Normal smoothing runs over the same normal groups.
The *Normal weighting* option selects the accumulation weight — face area
(meshopt's own), corner angle, or both multiplied (the Blender Weighted
Normal modifier analog). Tangents are re-orthogonalized against the final
normals so the saved frame matches the one the bake encodes against.

UV overlap repair must finish before the atlas is accepted. Its ordinary and
high-precision packs wait for the shared xatlas repack session on the Remesh
worker, with cancellation available while waiting. This leaves the Editor free
to finish an interactive owner. A waiting cancellation never releases or destroys
that owner's atlas. Optional merge and public repack calls retain their immediate
busy rejection. The existing overlap, stretch, padding and cost gates still apply.

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

The bake and Maps view store canonical RGB normals. The Result Lit preview uses
a separate linear GPU texture packed for the active shader decoder. Android's
DXT5nm setting enables AG decoding in URP, so its preview places normal X in alpha;
feeding a raw RGB texture with alpha 1 would make lighting depend on chart tangents.
Built-in and URP use their respective decoder precedence. The preview cache follows
the active target, normal encoding and pipeline, and releases its owned textures
when invalidated. This conversion does not alter baked bytes or PNG export.

GPU surface queries use bounded 65,536-query chunks; CPU bands remain at 16,384.
`RemeshDiag` reports band/query/dispatch counts and stage timings, including Editor
scheduling and asynchronous readback waits. GPU performance depends on query count
and Editor scheduling; it must be measured against CPU with identical bake inputs.

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
- Source snapshot/readback and export are synchronous editor operations.
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

`RemeshPreviewNormalPackingTests` renders the actual Result material against an
independent mesh-normal control, including neutral/tilted normals and rotated or
mirrored chart tangents. Run the graphics fixtures with Android DXT5nm and XYZ
normal encoding, and Built-in and URP. They also check canonical Maps bytes,
texture ownership and render-target restoration. `RemeshProjectionSurfaceTests`
checks mixed ray traversal and hit/miss slots across the GPU chunk boundary, plus
the actual Unity compute compiler's uninitialized-variable diagnostics.

Manual gate: textured multi-submesh prop, nested transforms including negative
scale, normal-mapped high-poly with bevels, thin sheet, LODGroup, 4K source texture,
missed projection, cancel/tab switch, each hard-edge mode, every preview view, a
Read/Write-disabled FBX, a non-Standard shader, and export/reimport in Built-in and URP.
Compare actual rendered output against the source and confirm the source assets
remain byte-for-byte unchanged.
