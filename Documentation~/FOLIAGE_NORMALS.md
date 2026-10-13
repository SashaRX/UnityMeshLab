# Foliage Normals

Open **Tools → Mesh Lab → Open Mesh Lab → Foliage Normals**. Select a scene root,
choose foliage renderers/materials/submeshes, and press **One Click: Build + Transfer**.
The tool builds a smooth volume around leaf cards and assigns explicit normals from
that volume to mesh clones. No scene objects are created for the spheres.

Use **Build Proxy**, **Smooth / rebuild**, **Remesh / rebuild** and **Transfer normals** for individual
stages. Every button runs any missing/stale prerequisites. A settings change clears
only its stage and subsequent results. **Clear Preview** releases the captured and
generated preview meshes. **Apply to Scene** and **Save Mesh Asset** become available
only after a successful transfer with at least one transferred vertex.

Enable **Relax transferred normals** in **Transfer** to smooth the final directions
after projection and blend, separately from proxy smoothing. **Normal relax iterations**
(0–100, default 3) and **Normal relax strength** (0–1, default 0.5) control this optional
step; it is disabled by default. Press **Transfer + relax normals** after changing them.
The existing proxy is reused, and every run starts from the projected result, so
repeating the action does not accumulate relaxation.

Relaxation averages unit directions along connected selected triangle edges in
capture space, independently in every selected LOD. Disconnected leaf cards and
vertex splits remain separate. Missed vertices and excluded faces do not supply
neighbour directions and retain their original normals; selected/excluded boundary
copies continue to protect the excluded side. Geometry and other channels stay
unchanged. Original-hemisphere alignment remains active when enabled, and tangents
are regenerated after relaxation when requested. Preview, Apply, both mesh-save
paths and FBX output use this same final result. Zero strength, zero iterations or
zero transfer blend preserve the unrelaxed output exactly.

## Normal source: voxel envelope, sphere or hemisphere

**Proxy shape** chooses the source surface for the same nearest-triangle normal
transfer. **Voxel Envelope** scatters overlapping spheres, voxelizes their union
and applies Taubin smoothing followed by isotropic surface remeshing as described below. **Sphere** and **Hemisphere**
directly generate an indexed surface with smooth surface vertex normals; scatter,
native voxelization, geometry smoothing and isotropic remeshing are skipped. They require no native
remesher, and CPU/GPU projection, blend, distance limits, boundary splits,
preview, Undo and asset saving use the common transfer/output stages.

Enable **Auto fit** and choose **Fit to**. **Selected Foliage** (the default, also
retained by older saved settings) uses only selected triangle vertices.
A sphere is centered on their capture bounds and its radius is the greatest
distance from that center to a selected vertex. A hemisphere's axis defaults to
root-local +Y; its center lies on the lowest selected point's plane along that
axis, with radius equal to the greatest selected distance from that center.

**Object Bounding Box** fits the shape to the full vertex bounds of the meshes
used to build the proxy, including their excluded submeshes. Renderer filters
still choose those meshes; unrelated renderers and transfer-only LODs do not
enlarge the box. With LOD0-only construction, all selected LODs receive normals
from that same fitted proxy. The transfer mask still changes only selected faces.
The sphere is stretched to the box's half-size on each axis (an ellipsoid); the
hemisphere uses the full box height and puts its open equator on the box's bottom
plane. Bounds are measured in root-local space, or along the rotated hemisphere's
axes. Its normals follow the curved surface's inverse axis scale, rather than
using the radial direction of a round sphere. Planar cards receive a small
thickness after the size multiplier is applied (0.01% of the largest box
dimension, at least 0.000001 capture units, increased when the fitted center
requires more float precision) to keep the transfer surface valid; a zero-size
box is rejected.

**Rotation (root local)** rotates the hemisphere and its fitting axis. It has an
open equator, with **no flat base cap**, so bottom faces cannot supply downward
cap normals. This intentional boundary is validated separately from the closed
voxel envelope. Points below the equator project to the curved surface or rim.

**Center offset** moves the automatic center before selected-vertex radius
fitting; for object-bounds fitting it translates the already fitted shape.
**Size multiplier** scales the fitted radii. Disable auto fit to enter an
absolute root-local center and radius in capture units. **Segments** controls
surface resolution (16–128 in multiples of four, default 64). Spheres have one
vertex per pole and shared ring vertices; hemispheres share their equator ring.
Normals are smooth surface normals, then interpolated at the nearest proxy triangle like all
other source modes; the final normal is not calculated directly from the target
position. Manual sphere/hemisphere settings affect Envelope and downstream
stages; changing source mode clears Scatter and downstream while keeping Capture.
Unused scatter/smoothing/remesh settings do not rebuild a sphere or hemisphere.

## Capture and coordinate spaces

Capture restores temporary toolbar previews before reading mesh channels and
material selections. In particular, Lightmap preview's transformed UV1 display
coordinates cannot become the saved UV0; its toolbar mode is turned off.

All processing takes place in the selected root's local space. Each static renderer
has an independent `FoliageTargetSnapshot`, preserving the source Mesh reference,
readable original snapshot, per-submesh index arrays and selection, materials,
original positions/normals, vertex usage masks and full matrices:

```
M = root.worldToLocalMatrix * renderer.localToWorldMatrix
capturePosition = M.MultiplyPoint3x4(localPosition)
normalLocalToCapture = M.inverse.transpose
captureNormal = normalize(normalLocalToCapture.MultiplyVector(localNormal))
normalCaptureToLocal = M.transpose
localResultNormal = normalize(normalCaptureToLocal.MultiplyVector(proxyNormal))
```

This includes nested hierarchies, nonuniform or negative scale and FBX axis
conversion. Root placement, rotation and scale never change. Proxy spheres and
distances are measured in capture units; a nonuniformly scaled root therefore
deforms the displayed world-space envelope. This is intentional root-local metric
behaviour, rather than a world-space isotropic sphere union.

`MeshAccess` reads imported meshes with Read/Write disabled. Foliage uses its raw
Editor MeshData capture to preserve attribute formats, vertex streams, index
buffers and submesh descriptors without toggling the source importer. Unreadable deforming
meshes whose full skin/blend shape streams cannot be preserved are rejected.
Normals must use Float32 with three components; packed normal formats and other
normal dimensions are rejected before capture so Apply/Save cannot silently
change their storage layout. Tangent regeneration requires any existing tangent
channel to use Float32 with four components. Disable **Recalculate tangents** to
retain a packed tangent channel. Regeneration runs on an owned scratch mesh,
preserving packed positions/UV0 and other raw streams in the result.
Sources must contain finite positions and authored unit normals at every vertex.

The three face filters are all triangle submeshes, selected material references,
and selected submesh numbers. Disabled/inactive renderers, collision names from
`MeshNaming` and non-triangle submeshes are excluded. Selecting a renderer also
selects its corresponding mesh in every LOD of the same LODGroup, or its named
LOD siblings under the same parent. Other mesh families remain untouched, even
when renderer arrays have different ordering between LOD levels. Selecting an
individual LOD object as Root also includes its corresponding sibling levels.
Renderer names identify a mesh family only when they are unique within the
selected level. If those names repeat, distinct source mesh names can identify
the matching parts without including a neighbouring mesh.
With Choose renderers disabled, all eligible mesh families are used. When enabled,
an empty selection is an error and cannot silently process all renderers.
**LOD0 proxy source** limits proxy construction to the lowest available LOD of
each selected family; normal transfer, Apply and both Save actions include every
matching LOD. Turning it off also includes the other LODs in proxy construction.
Selected submesh numbers refer to the lowest selected renderer in each family.
Their material references identify the corresponding submeshes in other LODs,
so reordered material slots do not change excluded geometry. A missing selected
material leaves that LOD untouched. Ambiguous mesh names or selected/excluded
submeshes sharing a material are rejected rather than widening the selection.
Separate one-submesh LOD meshes and instances sharing the exact same source mesh
can retain the selected index without material matching.
**Preview LOD** displays one level at a time; switching it does not limit Apply or
Save, which always use the complete captured selection across all matching LODs.
SkinnedMeshRenderer is not a write-back target.

## Surface sampling

Selected geometry is combined for scatter alone; renderer vertex correspondence
never comes from the combined geometry. `MeshGeometry.WeldPositions` joins exact
position copies before a union-find builds connected face islands. UV or normal
splits consequently do not separate one leaf card. Degenerate triangles are
counted and excluded.

Triangle area is `0.5 * length(cross(b-a, c-a))`. For island area A and spacing h,
the surface sample count is `max(minimumSamplesPerIsland, ceil(A / h²))`. Triangle
choice uses stratified points `(i+0.5)/count` in the island's area CDF. Two seeded,
scrambled radical-inverse dimensions (bases 2 and 3) supply u and v in [0,1).
The exact uniform barycentric formula is:

```
r  = sqrt(u)
b0 = 1 - r
b1 = r * (1 - v)
b2 = r * v
p  = a*b0 + b*b1 + c*b2
```

Optional source vertices and triangle centroids are added. A spatial hash removes
samples closer than `h * dedupSpacingFraction`, checking neighbouring cells by
actual Euclidean distance. Every island reserves its minimum first. Minimum
coverage takes priority over dedup distance: dense neighbouring cards and tiny
islands retain their required samples even when those samples are close. Further
samples still obey the global dedup distance. Diagnostics report how many close
samples were retained for island coverage. After dedup,
the primitive cap retains island minima plus a deterministic evenly distributed
subset of the remaining samples. Counts, surface area, skipped degenerates and
exact nearest-neighbour min/mean/max are reported. Neighbour diagnostics reuse
`TriangleBvh` with point triangles and self-face exclusion.

Defaults: spacing 5% of selected capture diagonal, radius = spacing, one sample per
island, source vertices on, centroids off, seed 1729, primitive budget 20,000.
Scatter refuses more than one million candidates. The hard primitive cap is
50,000; actual geometry also has a two-million-triangle/128 MiB input budget.
Increase spacing or reduce sphere subdivision if the geometry budget is exceeded.

## Voxel envelope

`FoliagePrimitiveBuilder` expands one shared unit icosphere template (subdivision
0 or 1) into one geometry-only indexed triangle soup. No sphere GameObjects,
normals, UVs or tangents are needed. Mesh previews use UInt32 indices when needed.

`IFoliageProxyBuilder` is implemented by `MeshoptVoxelProxyBuilder`. It calls
`RemeshNative.Voxelize` / `meshLabVoxelRemesh` using a separate RemeshSettings
snapshot: solve=false, shell=false, trimToSource=false, simplify=false. The existing
native bridge owns and releases native handles, including after cancellation.

The reference semantics are `d(p) = min_i(length(p-center_i)-radius_i)`, surface
`d(p)=0`. This implementation voxelizes tessellated spheres, approximating the
union. It does **not** retain a dense signed-distance grid or SDF asset. The UI
calls the result **Voxel Envelope / Volume Proxy**. Subdivision 0 has visible
icosahedral approximation error; subdivision 1 is the default. Voxel bounds are
validated against the actual primitive union within 1.5 voxel cells. Reference
EditMode tests additionally compare subdivision-1 spheres with analytic union
bounds and check overlap has no internal surface.

Every result must be nonempty, finite, correctly indexed and closed/manifold under
`RemeshTopology.Inspect`. Signed volume orients each disconnected component outward;
an inverted component cannot hide behind the volume of another one. One sphere,
two overlapping spheres and two separated spheres are explicit backend tests.
Separated foliage clusters legitimately produce several closed components in one
indexed proxy. No secondary voxelizer is used.

## Smoothing

Taubin geometry smoothing builds sorted vertex adjacency and alternates positive
lambda and negative mu Laplacian passes. Defaults are lambda=0.5, mu=-0.53,
strength=1 and five iterations. Topology/indices remain unchanged. Finite geometry,
degeneracy, closed topology and outward component volume are validated afterward.
The operation fails if any connected component's volume drops below half its
original volume or grows above 150% of it, even when a larger component keeps the
total volume stable.
Relative total volume change over 15% is reported as a warning. Bounds and signed volumes before/after
are retained in diagnostics. This is shrink-compensated smoothing, not an exact
volume constraint or a self-intersection solver.

`MeshGeometry.AveragedNormals` generates smooth area-weighted proxy normals. Proxy
normal data is validated for finite unit directions. Proxy hard edges are never
introduced.

## Uniform triangle proxy remeshing

**Uniform triangles** is enabled by default for the voxel envelope. The separate
Remesh stage runs **after Taubin smoothing and before normal transfer**, on the
proxy alone. It changes proxy topology; source leaf-card geometry is untouched.
`IFoliageProxyRemesher` uses the original managed `FoliageProxyRemesher`
implementation of incremental isotropic surface remeshing. No libigl/CGAL code,
TetGen dependency or tetrahedral volume is embedded. The standard surface
algorithm consists of edge split/collapse/flip, tangential relaxation and
projection ([CGAL algorithm reference](https://doc.cgal.org/latest/PMP_Remeshing/group__PMP__local__remeshing__grp.html)).

For target edge length h, each iteration:

1. Splits edges longer than `4h/3`, projecting their midpoints onto the smoothed
   reference surface through `TriangleBvh.FindNearest`.
2. Collapses edges shorter than `4h/5` when the manifold link condition, face
   orientation, duplicate-face check and resulting edge-length constraints pass.
3. Flips interior edges to improve valence towards six and triangle shape.
4. Moves vertices towards their neighbour centroid in the tangent plane,
   then projects them onto the unchanged smoothed reference surface.

Projection rejects a hit on another disconnected reference component. All edits
reject degenerate/inverted incident faces. Sorted candidates and neighbours make
the result deterministic. Adjacency is updated locally rather than scanning all
faces per edit; recycled face and vertex slots bound working storage. All array
processing occurs in Task.Run, with cancellation checks throughout each pass.
No temporary Unity objects or compute buffers are allocated by this stage.

**Edge length (0 = auto)** is in root-local capture units; zero uses the smoothed
proxy's mean unique-edge length. Defaults: five iterations, relaxation 0.5,
100,000-triangle budget (hard cap 200,000). **Proxy triangle budget** is configured
in Envelope and also applies when Uniform triangles is disabled. The voxel result
is checked before publishing its preview, and smoothing checks it before building
adjacency. Changing this budget clears Envelope and downstream results. Remeshing
checks input and estimated ideal triangle counts before creating mutable adjacency;
each split checks the budget again. Working adjacency costs approximately 1 KiB
per triangle, varying with valence and allocator overhead. An oversized Envelope stops
before preview and smoothing, reports its actual triangle count and keeps the chosen
voxel resolution. **Set budget to ... and rebuild** explicitly raises the budget to
that count (up to 200,000) and rebuilds using the cached samples. Alternatively,
reduce **Voxel resolution**. The manifold occupancy fallback can produce more
triangles than the initial rejected voxel surface at the same resolution. Changing
remesh target edge length cannot fix an Envelope budget refusal; it only helps
when the later remesh stage exceeds its budget. Source modes Sphere/Hemisphere keep their
radial normals and bypass this stage. Disabling Uniform triangles passes the
smoothed proxy through unchanged.

`RemeshTopology.Inspect` verifies the final closed manifold and component/Euler
counts; every component must retain positive signed volume. Finite unit vertex
normals are regenerated by `MeshGeometry.AveragedNormals` on the final geometry.
Surface deviation is measured in both directions at vertices and face centroids
using BVHs; deviation over `h/2` or volume change over 25% in any source component
fails the stage. Source component labels survive topology edits and compaction,
so a larger shell cannot hide distortion of a smaller one. Total volume change
over 10% is a warning. These sampled distances are diagnostics,
not a certified continuous Hausdorff bound or a complete self-intersection test.

Diagnostics compare triangle counts, edge-length and triangle-area coefficients
of variation (standard deviation / mean), minimum angle and mean triangle shape
`q = 2√3 * |cross(b-a,c-a)| / (|a-b|²+|b-c|²+|c-a|²)` (equilateral = 1).
They also report split/collapse/flip/rejection counts, projection deviation and
total and worst-component volume change. The target is approximately uniform
triangles; tight curvature and topology constraints can retain short edges. Inspect **Smoothed Proxy**
versus **Transfer Proxy**, with **Show proxy edges**, and tune h/iterations.
Changing remesh parameters invalidates Remesh and Transfer only, preserving
Capture, Scatter, Envelope and Smooth.

## Normal Thief transfer

The final remeshed proxy feeds the existing `TriangleBvh` (the smoothed proxy when
remeshing is disabled, or the radial sphere/hemisphere). CPU queries use `FindNearest`;
GPU batches use `GpuBvh.TryCreate` and `NearestAsync`. Neither casts rays along the
old card normal. Both resolve nearest triangle, closest point, squared distance
and barycentric weights. GPU edge hits, hits near the distance limit and misses
are canonicalized through the same CPU BVH to avoid floating-point face choices
on shared edges. The number and CPU cost of these canonical queries are reported.
GPU availability fallback is logged and the actual backend is displayed; GPU
dispatch/readback errors fail the stage.

```
n = normalize(proxyNormals[i0]*bary.x
            + proxyNormals[i1]*bary.y
            + proxyNormals[i2]*bary.z)
if (alignToOriginalHemisphere && dot(n, originalCaptureNormal) < 0) n = -n
n = normalize(lerp(originalCaptureNormal, n, transferBlend))
localNormal = normalize(M.transpose.MultiplyVector(n))
```

Hemisphere alignment defaults off, matching normal theft from the proxy. Blend
defaults to 1. Blend 0 and projection misses retain the exact original local
normal. A cancelling blend or invalid hit counts as a miss and never writes a zero
normal. Max distance 0 means twice the selected capture diagonal. Automatic
Object Bounding Box fitting also includes the fitted proxy bounds in that diagonal,
so excluded source geometry can enlarge the proxy without preventing transfer.
Positive values are explicit capture-unit distances. Zero transferred vertices fail
the pipeline.

## Selected/excluded boundary vertices

A vertex used by both selected and excluded faces is appended once for selected
faces; only selected submesh indices are remapped to the duplicate. Original
vertices and excluded normals remain intact. Raw MeshData vertex streams are
copied by stride, preserving every stored attribute/dimension/format, UV0–UV7,
HDR colours and tangents. Variable bone weights, bind poses and blend-shape frames
are copied separately. Materials, submesh topology, face order and bounds remain.
Any unsupported capture or copy fails rather than modifying trunk normals.

Tangents are recalculated only if requested and UV0 exists, is finite and has
nondegenerate UV triangles on the selected faces. Excluded UV degeneracy does not
disable foliage tangent recalculation. Without valid selected UV0, original tangents are retained and
a warning is emitted. Existing excluded tangent values are restored after
recalculation. If the source has no tangent attribute, newly generated frames are
kept only on selected vertices; excluded vertices retain the zero default frame.
Target normals are never regenerated with RecalculateNormals.

## Preview, apply and assets

**Follow selection** updates the Root and preview when a different tree is selected
in the Hierarchy; choosing a LOD child resolves its LODGroup. Disable it to keep a
manually assigned root. A source change cancels an active calculation, waits for
its cleanup, and then displays the latest selected tree. Selecting a light, camera
or project asset keeps the current tree. Preview works before Capture.

**Selection Mask** paints the exact selected faces green when they supply the proxy,
amber when they only receive transferred normals in another LOD, and grey when
excluded. This includes excluded renderers for context; shared vertices on a
selected/excluded submesh boundary do not blend the mask colours. Mask colours
remain visible with any shared viewport shading mode. **Preview LOD** can inspect
this selection before building the proxy.

The shared `MeshViewport3D` also supports source cards, primitive sample points,
voxel proxy, smoothed proxy, remeshed transfer proxy and transferred cards.
After **Scatter samples**, **Primitive Samples** immediately overlays translucent
spheres on the main mesh at the computed radius, with green sample centres. The
panel reports radius in capture units and the number of visible spheres. **Sphere
opacity**, **Sphere surface** and **Show sphere edges** control this display; edges
are off by default. Up to 4,096 evenly distributed spheres are cached per Scatter
result, while all primitives still participate in building the envelope. The
preview is rebuilt when Scatter inputs change and released with that result.
**Proxy + mesh** also shows these spheres before an envelope is available.

**Proxy + mesh** overlays the latest available proxy as a translucent cyan surface
(default opacity 25%) with optional edges. **Proxy opacity** changes only the proxy;
the main mesh retains its materials and chosen shading. Disable **Proxy surface**
for wire-only display, or **Show proxy edges** for a surface-only overlay. The proxy
stays out of mesh inspection picking and UV overlays. **Before** / **After** switch
original and transferred normals in the same position without moving the camera;
the proxy remains visible when switching in the combined mode. **Side by side**
shows original on the left and transferred on the right. Choose **Normals** shading
in the shared 3D toolbar for direction colours, or enable **Normal directions** for
cyan original / yellow transferred arrows on selected vertices. Arrow length is
adjustable; side-by-side mode draws both sets. Primitive points are bounded to
4,096 per frame. **Show proxy edges** uses cached wire drawing for the three solid
proxy previews and the combined view. Source copies and selection paint are cached between changes,
prepared outside GUI repaint, and released on source changes or deactivation.
Transient meshes use HideAndDontSave and never replace scene meshes during preview.

Apply checks captured Mesh references, a content fingerprint of all vertex streams
and indices, bounds and import settings, matrices, material assignments and the
current eligible renderer set. UI availability caches this expensive check until
a pipeline, hierarchy, project, Undo or relevant vertex-channel change event;
Apply and both Save actions always perform their own fresh check. In-place mesh edits, Undo or reimports require
recapture before applying or saving. Renderers with additional vertex streams are
refused before capture because those attributes can override the result or become
incompatible with boundary copies. Apply creates scene-owned result clones and records renderer and
MeshFilter changes in one Undo group. Prefab instance overrides are recorded.
Clearing previews or rebuilding cannot destroy applied meshes; Undo/Redo owns
their scene lifetime. The hub restores only its owned temporary mesh swaps, so
Undo/Redo, switching selection and closing Mesh Lab preserve applied scene meshes.
Apply is disabled in Prefab Mode: save a persistent Mesh Asset or Separate FBX and
assign that asset to the prefab instead. This prevents saving a transient mesh
reference in a prefab. **Save Mesh Asset** reserves unique output paths before its
asset-editing batch, including when several renderers share one source mesh, and writes separate `_FoliageNormals.asset` meshes under an
Assets folder. **Save Separate FBX** writes one unique `_FoliageNormals.fbx` per
source model into the chosen Assets folder. It requires Unity FBX Exporter
(`com.unity.formats.fbx`); the button is disabled with an installation hint when
that optional package is absent. Original FBX files and their importer settings
are never overwritten. Saving does not assign new meshes or change the scene.

FBX saving delegates to the existing `FbxExport.WriteChannels` / `FbxChannelWrite`
document writer with **Normals | Tangents** intent. The SDK loads the source
document and edits only selected foliage polygon corners' normal/tangent/binormal
elements, using the tagged import, `FbxCornerMatch` and `FbxSpaceFit` for exact
corner correspondence and axis/unit/pivot conversions. The submesh mask protects
excluded corners even where positions, UVs and colours coincide. Boundary copies
in the working Unity mesh become independent FBX corner normals; source FBX
control points and polygons are not split or rebuilt. Existing excluded normal
values remain exact. UV sets (including names and precision), vertex colours,
control points, polygons, material assignments, nodes, transforms and deformers
are inherited from the source document. New normal/tangent elements, if absent,
must cover every corner and use the unchanged original shading on excluded faces.

The new FBX inherits source import settings and material remaps, with **Normals =
Import**. The source tangent mode is retained: authored tangents remain imported;
MikkTSpace and legacy calculations remain calculated. It never switches excluded
faces from calculated shading to a stored FBX frame. **Tangents = None** remains
supported for normal-only results. A result containing regenerated tangents is
refused with that source setting because the new importer would discard them;
disable **Recalculate tangents** and rebuild, or enable source tangent import and
recapture. Only the new importer is configured. A source using calculated normals is refused: switching
the variant to imported normals could otherwise change excluded shading. Use an
FBX source with Normals = Import and recapture; standalone/generated meshes
without an FBX source are also refused. Ambiguous corners, inconsistent edits of
instances of one FBX mesh and incompatible submesh layouts fail explicitly.
Shared FBX geometry requires every referencing source node to be captured with
compatible shading. Node identity comes from the original model prefab, so
disconnected copies or ambiguous source node names are refused for shared
geometry. Capturing the same node from multiple model instances does not stand
in for a missing source node. This prevents filtered-out FBX instances from
receiving foliage shading through their shared mesh.
Writes use the shared atomic temporary-file mechanism; failed outputs are removed.
Either variant import failing aborts the save, including the second import after
copying source settings. A written file alone cannot count as a successful export.
Repeated saves choose unique filenames. FBX serialization can change headers and
SDK metadata; this is semantic preservation of untouched scene/mesh data, not a
byte-identical copy of the entire file.

Source Mesh Compression settings are retained on the new importer. Unity's lossy
compression can requantize normals/tangents after their range changes, even on
excluded faces whose stored FBX values are unchanged. The tool reports this when
the source uses compression. The reference pine uses Low compression; document
checks verify exact excluded FBX normals and all untouched data, and uncompressed
throwaway imports verify their exact Unity directions. Set Mesh Compression Off
on the new FBX for uncompressed shading; this does not edit the source model.

## Stages, cancellation and diagnostics

Capture → Scatter → Envelope → Smooth → Remesh → Transfer each has a settings key. One run
per pipeline is allowed, and reload is locked until the run exits. Array workers
run through Task.Run; Unity object access, GPU dispatches, mesh writes and asset
saves occur on the main thread. Cancellation waits for in-flight native calls and
GPU readbacks. Dispose during a run requests cancellation and defers cleanup until
the worker exits. Failed/cancelled stages clear incomplete downstream outputs.
Reload/deactivation unregister callbacks and dispose previews; no static pipeline
state survives reload.

Status shows stage/time, primitive count, proxy triangles/components, transferred
and missed vertices. Transfer diagnostics include renderers, selected faces and
vertices, misses/percentage, invalid hits, hemisphere flips, duplicated boundary
vertices, mean/max projection distance, actual CPU/GPU backend, canonical query
count, and CPU/GPU timings. Timings include asynchronous scheduling/readback costs
and vary by hardware/editor load.

## Validation and limitations

EditMode fixtures cover sampling, primitive budgets, native sphere unions,
shrink-compensated smoothing, isotropic remesh uniformity/density/determinism,
closed components, budgets, cancellation and deferred disposal, explicit normal interpolation, boundary channel
copying, FBX-style matrices, GPU parity/cancellation, stage invalidation,
Apply Undo/Redo, imported unreadable capture, asset saving and repeated runs.
Sphere/hemisphere fixtures verify radial interpolation, indexed topology,
the intentional open rim, rotated base-plane fitting, CPU/GPU parity,
selected-only fitting, cancellation, Undo and stage/resource lifecycles.
The synthetic crown uses 96 independent cards with varied plane normals. Its
test optionally writes rendered previews and CPU/GPU metrics when
`MESHLAB_FOLIAGE_OUTPUT` names an output directory outside Assets.

`FoliageReferenceAssetTests` is an opt-in check for `Forest_pine_03_Optimized.fbx`.
The model is not distributed with the package. Import a reference Unity package
containing it at `Assets/FoliageReferenceInput/Forest_pine_03_Optimized.fbx` with
Unity's `-importPackage` argument, then set `MESHLAB_FOLIAGE_REFERENCE_PACKAGE`
to that package path and `MESHLAB_FOLIAGE_OUTPUT` to a directory outside Assets.
Run it in a disposable test project: the fixture removes its input/output asset
folders after exporting previews, metrics and a separate result Mesh package.
It reproduces the verified material slots with portable test materials:
slot 0 `M_Pine_trunk_05_Optimized` is excluded; only slot 1
`M_Pine_leaves_Optimized` contributes to the proxy and receives new normals.
It checks every excluded normal/tangent, all UV channels, positions, indices,
material references, transforms, unchanged FBX/importer bytes, CPU/GPU agreement
and Apply Undo/Redo. Preview colours visualize normals without the source HDRP
shader or alpha textures.

The pine profile uses spacing at 4% of the selected bounds diagonal, radius
multiplier 1.6, dedup fraction 0.01, voxel resolution 64 and three Taubin iterations
at strength 0.5. This recorded profile uses a smaller dedup distance; island
minima are now retained even for densely adjacent leaves. Finer profiles tested on this model produced invalid native topology
(point/edge contacts); validation rejected them before Transfer. Increase
primitive overlap or change voxel resolution when that diagnostic occurs.

Version one supports static MeshRenderer targets; normal sources are a voxel
union of sphere primitives, an explicit sphere or an open hemisphere.
It samples geometric cards, ignoring alpha textures. There is no runtime
component, wind, skin write-back, capsule/ellipsoid, dense SDF Texture3D, UV unwrap,
material bake or automatic whole-scene processing. Low voxel resolutions can
merge close clusters; wide spacing can leave disconnected volumes. Thin gaps and
overlapping cards require tuning spacing/radius/resolution. Smoothing validates
topology and volume but does not detect all geometric self-intersections.

The shared footer **Overwrite FBX** uses the current Foliage transfer result
without requiring **Apply to Scene** first. It confirms the source files, writes
only selected normals/tangents in the captured LODs through the channel writer,
and preserves other channels and hierarchy. **Export New FBX** uses the separate
Foliage FBX workflow. A missing or stale transfer disables the footer actions;
the hub never substitutes unrelated context meshes. Successful overwrite relinks
imported meshes and clears the old capture, so rebuild before another transfer.
An import failure after the atomic file commit is reported as a failed operation;
the source is retained and can be reimported after fixing the importer failure.
