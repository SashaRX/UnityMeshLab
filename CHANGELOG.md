# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Local LOD screen diagnostics measure thin-detail/visible-component loss and sharp RGBA-boundary retention at three footprints, with explicit unevaluated cases. Actual-model readback replay and a fresh tent capture pass 196 selected tests; diagnostics expose regional regressions without changing simplification defaults.
- Optional native LOD crease constraints permit patch retriangulation while protecting shading seams, junctions, material borders and ambiguous faces; failed coverage retries locked chains, then reports the strict-belt fallback. CI-built native plugins and 183 selected Unity tests cover the additive ABI and fresh eight-model source-budget/matched-count comparisons; measured regressions keep the option disabled by default.
- Optional LOD crease-chain coarsening preserves tracked feature shape, separate shading sides, material junctions and topology; coarsened native retries rank sampled source quality. Actual-model evaluation keeps the strict protected baseline and verifies DPI-independent GPU captures.
- Collision Coarse Parts preset analyzes disconnected elements using oriented bounds and closed-volume fill, fits boxes to dense elements, and tries per-element remesh plus geometry-only decimation for other shapes. Replacements pass topology and bidirectional sampled surface-fit checks, with source decimation or original geometry as fallbacks. Element Analysis reports preparation choices, fill, size and error. Elements are connected across geometric edges rather than isolated shared points; nearby groups use triangle-surface distances, including edge-to-edge approaches. Similarly oriented elements share a global convex hull budget between independent groups; volume error, part gap and part angle are adjustable. Detailed whole-mesh decomposition remains available; completion logs distinguish source meshes, collider meshes and triangles.
- LOD Gen supports source-based LOD1/LOD2 triangle budgets, measured candidate selection, verified normal/RGBA correction, conservative full-loop and small-part options, and authored hard-edge protection. Project-model evaluation and the next-iteration plan document protected-budget limits and visual regressions.
- Rejected LOD vertex-color fits can use verified area-weighted source resampling; connected equal-color UV duplicates share fitted RGBA. Regeneration preserves prior generated levels on cancellation or failure through a shared Undo transaction.
- Universal mesh inspection shares the 2D/3D layout, projection and attribute shading. The Inspect panel is a model-level summary — vertex/triangle counts, size, present channels only (no skinning or blend-shape noise on static meshes) and per-channel UV ranges — with the shading dropdown offering only the modes the shown meshes carry data for; Ctrl+click a surface to inspect that mesh. Remesh source meshes are inspectable before baking; checker and UV island borders work in 3D.
- Tools declare required libraries through `MeshLabTool`; project settings control module availability without resetting unrelated active tools. CI checks tool dependency boundaries.

### Changed
- Split shared UV setup/quality panels, pipeline auto-tune, Hier UV2 packing and FBX import/export into explicit stages. Preserve native session cleanup, cancellation, import replay, Undo ordering and allocation-free inspection loops.
- Shared export, UV workflow and meshoptimizer simplification live in libraries independently of tool tabs. LOD generation batches the common simplifier; generated results are tracked by the context for export cleanup.
- Removed the obsolete standalone Python/BAT sweep-gallery generator. It only understood the retired flat `BenchmarkReports/*_sweep_*.csv` + `{csvBase}_png/` layout; sweeps now generate their own `index.html` in the current nested run layout. Benchmark documentation and hierarchical-repack artefact descriptions now name the files the current code actually writes.

### Fixed
- Clearing or regenerating collision previews releases cached wires, line ribbons and pending uploads before destroying their source meshes. Existing collision containers are detected throughout nested hierarchies, preventing duplicate application and preserving container-level Undo removal. Reapplied mesh assets retain canonical collision names even when their filenames must be unique.
- Collision sidecars retain the imported source mesh name and selected LOD after Optimize or UV weld creates a working copy, preserving source fingerprints and avoiding FBX collision placement failures caused by `_wc` names.
- Collision results now show surfaces and per-hull wire in the shared 3D preview, using each source renderer's transform in both previews. Applying colliders preserves nested scale, reflection and the root's layer, supports Undo/Redo, and prevents duplicate batches. Sidecars remain source-local for document saves; rebuilt FBX exports use source frames captured before normalization. Collision simplification ignores surface attributes and material seams, welds exactly coincident positions, and supports Read/Write-disabled meshes. Convex hulls are capped at 128 vertices and checked against Unity's 255-triangle limit.
- 3D preview wire, UV borders, cage lines and floor use screen-space triangle ribbons with analytic coverage, including rounded ends, subpixel opacity and near-plane clipping. Exact duplicate segments at split vertices no longer darken the wire; persistent ribbons are built asynchronously and released with their source or viewport. URP previews use a single-sampled 24-bit-depth target without modifying the shared pipeline asset: the multisampled target still produced `Missing resolve surface` / `Not inside a Renderpass` on Unity 6000.2.6f2 with URP 17.2 even with matching sample counts. Built-in retains 4x silhouette MSAA; SRP line smoothing is independent of MSAA, but SRP silhouettes are not multisampled.
- The hub's 3D viewport no longer floods the Console with "Missing resolve surface" / "Not inside a Renderpass" errors under URP: URP renders a camera multisampled only while its asset enables MSAA, with the sample count of the camera's target, and the viewport's multisampled target did not match an asset with MSAA off. An asset with MSAA off now has it switched on for the viewport's render only and put back after, so URP renders the viewport multisampled and the viewport resolves it as with the built-in pipeline. The viewport renders at 4x instead of 8x on both pipelines. A pipeline without that setting (or URP's deferred renderer, which has no MSAA) renders single-sampled into a 24-bit-depth target.
- Saving UV sets and vertex colours into an FBX (UV transfer, UV1 pack, vertex colour / AO bake, the hub's Overwrite / Export New FBX when only channels changed) edits the FBX document through the FBX SDK instead of re-exporting it through Unity's FBX Exporter. Only the changed corners of the changed channels are written: quads and n-gons are no longer triangulated, vertices are not split, unchanged vertex colours keep their full precision instead of being quantised to 8 bit, UV channels keep their numbers, and the file keeps its format and FBX version. The hub's save also writes generated LODs, sidecar collision and meshes with edited faces into the FBX document: each new node sits next to its source node with the same transform and materials, existing nodes, names and polygons are left as they are, and only a node a new one replaces by name is removed. Changed normals and tangents (Cleanup's recalculation) go into the mesh's own normal / tangent layer, changed corners only, mapped back through the mesh's import, where the importer reads them from the file (Normals / Tangents: Import; under Calculate Unity recomputes them and the change is only logged); changed renderer materials go into the node's material slots, mapped to the Unity assets on the importer, when the importer imports materials. Neither needs a rebuild anymore. When the document save is not possible (e.g. the LOD0 node has no `_LOD0` suffix, the source's transform is animated, or a collision mesh Cleanup flagged), the save says why and offers the rebuild or saving channels only. Rebuilt meshes keep their source's UV set names (read from the file), carry a UV1 or vertex colours MeshLab added even when the file had none, and leave out zero-area faces. Removing UV1 also removes it from a persistent sidecar, and a save of several FBX files reloads the scene once, after the last.
- The 3D canvas renders with 8x MSAA into a 24-bit-depth target, so the wire, the grid and the UV borders are smooth lines that no longer fight the surface; the wire sits above the UV layer and flat overlays instead of being covered by them, and the ground grid has its fade and every-fifth-line accent back. The UV layer's wire takes the shading-dependent colour of the viewport's own.
- The checker, 3D shells and lightmap previews follow a LOD switch from the canvas toolbar instead of staying on the renderers the switch just hid, and the UV channel picked for the canvas returns when the new LOD carries it.
- The UV overlap scan certifies an atlas of any size: a uniform grid tests every pair once instead of an x-sweep whose fixed comparison budget ran out past some twenty thousand faces and made the mandatory overlap repair refuse the unwrap. Chart merge keeps its last validated checkpoint when a later packing probe throws, instead of falling back to the unmerged atlas.
- GPU projection: a BVH deeper than the compute traversal stack stays on the CPU instead of silently missing hits, readbacks that never complete fail after two minutes instead of holding the bake and its buffers, a failed query-buffer allocation no longer leaves the capacity claiming buffers that do not exist, and a degenerate source triangle returns its nearest vertex on the GPU as on the CPU.
- Remesh diagnostics write the `remesh_*.bin`, `unwrap_*.bin` and `repack_*.bin` captures to `%TEMP%/meshlab-uvmerge` only at the Verbose log level; at Info every run wrote its full source geometry to disk. Source surface refinement builds its source BVH and feature edges once per Simplify instead of once per pass, keeps a sub-tolerance vertex move it accepted, and the final-normal pass names a missing unwrap channel instead of failing on a null reference. The Remesh source preview bakes a skinned source at its current pose, as the capture does.
- Viewport regressions respect localized UV-range formatting and compare shader-sampled attribute colors after sRGB decoding. Cover English, Russian and Hungarian locales plus default, linear and sRGB render targets.
- Address CodeRabbit/Graphify findings: restore failed FBX overwrites and importer state, export computed channels, isolate all xatlas sessions, keep GPU cancellation terminal, preserve canonical inspection entries, align lightmap picking and fill visibility, deduplicate wire edges and retain prefab LOD overrides. Skinned source previews apply renderer scale once.
- GPU BVH kernels compile correctly and unsupported kernels are rejected before dispatch; queries and normal/cage calculations retain precision on small meshes.
- Remesh previews use the source root's axes; saved output preserves captured source orientation. Mesh asset saving clones working meshes so window cleanup cannot destroy persisted output.

## [1.1.26] - 2026-10-06

### Added
- 3D preview has X Up, Y Up and Z Up choices for its perspective camera and floor. The choice persists across window restarts and applies consistently to every displayed stage without transforming mesh/export data or resetting pan/zoom. Planar XY/XZ/YZ views keep their coordinate planes.

### Changed
- The 3D Grid toggle is labelled Floor and can hide the reference floor grid independently of surface wireframe. Its enabled state remains saved with window preferences.

## [1.1.25] - 2026-10-06

### Changed
- Simplify exposes an Error only button and shows whether a triangle count will stop reduction early. Per-node diagnostics report input/output/source counts, the requested stop count and native collapse error. Existing settings and geometric quality gates are preserved.

## [1.1.24] - 2026-10-06

### Fixed
- Release Vertex Color Baking and Prefab Builder preview materials on tool deactivation. Track and destroy only owned preview meshes, including after their scene objects are deleted or their mesh assignments are replaced. Restore partial previews as well as fully activated ones.
- Explicitly destroy cached checker, shell-color and UV PNG resources before assembly reload or editor shutdown, restoring scene references first. Recreate caches on demand.
- Release readable mesh copies when CPU AO baking/correction is cancelled, returns early or throws. Destroy a partially populated readable mesh when mesh-data capture or importer fallback fails.

## [1.1.23] - 2026-10-06

### Fixed
- Native Simplify retains positive-area sliver faces instead of opening holes during cleanup and forcing all managed retries to fall back to dense remesh geometry. Staged Unwrap also retains these faces and rejects an unsupported atlas mapping explicitly instead of silently deleting surface faces. Topology, surface-fit and quality gates remain enabled; native binaries come from the matching Build Native Libraries run.
- Save All creates every missing parent of its output folder through AssetDatabase, normalizes path separators, and refuses invalid paths or files occupying a requested folder. Existing folders keep their GUIDs.

## [1.1.22] - 2026-10-06

### Fixed
- Remesh 3D previews remove the scene root's position and rotation in every stage, including Source before running the pipeline. Root scale and child-relative transforms remain visible; generated stage and cage previews use the captured display frame. Saved prefab/FBX transforms continue to use the captured export frame.

## [1.1.21] - 2026-10-06

### Changed
- GPU surface projection uses 256k query bands on GPUs reporting more than 1 GiB of memory, retaining 64k bands on low-memory/unknown devices. Projection upload, readback callbacks, result copies and continuation delays are reported separately.
- Source refinement queries independent surface-distance probes in parallel with bounded storage and the original serial floating-point reduction. Vertex fitting reuses nearest answers only while the exact query position is unchanged; all quality gates, motion backtracks and collapse comparisons remain enabled. Per-phase Simplify timings expose the remaining work.

### Fixed
- Window preference saving uses an optional tool contract instead of a direct Hub dependency on the Remesh tab, restoring the CI dependency guard.

## [1.1.20] - 2026-10-05

### Changed
- Pipeline GPU baking through two bounded query bands: overlap projection/readback with ordered CPU evaluation and request preparation. Compute ray hits and the same nearest-surface fallback in one GPU dispatch, without the CPU miss-mask round trip. Preserve samples, order, tangent transport, AO and quality checks; drain both readbacks and CPU work on cancellation. Log overlapping stage spans separately from pipeline wall time.

## [1.1.19] - 2026-10-05

### Fixed
- Restore Mesh Lab window preferences per Unity project after closing the window or reloading scripts: active tool, panel widths and scroll positions, UV/3D display controls, per-tool UV fill, and Remesh/Bake preview toggles and foldouts.

## [1.1.18] - 2026-10-05

### Changed
- Calculate independent 3D bake footprints and coarse source-fit motion candidates in parallel. Preserve sample ordering, vertex update ordering, every candidate/trial, projection settings and quality gates. Retain prefetched footprints across query cutoffs so their diagnostics are counted once.

## [1.1.17] - 2026-10-05

### Fixed
- Normalize saved static FBX geometry at its original pivot with identity node transforms while preserving world size, orientation and tangent frames. Bake accumulated root and nested transforms into separate mesh copies before resetting ancestors. Normalized FBX uses meters and Z-up axes for a meter-based 3ds Max scene; Unity reimport bakes axis conversion without an extra object scale. Embedded maps survive conversion. Skinned hierarchy normalization is rejected before modifying the export clone.

## [1.1.16] - 2026-10-05

### Fixed
- Preserve positive-area voxel triangles below the model-relative Simplify cleanup threshold. Dense solid remesh outputs stay closed before source trimming, with and without source fitting; exact collapsed faces and non-finite data remain rejected by cleanup/topology guards. Native plugins require the matching Build Native Libraries result.

## [1.1.15] - 2026-10-05

### Fixed
- Give preview stage buttons sufficient height for separate title and status labels instead of clipped multiline mini-button text. Keep the progress strip clear of the selected-stage border and include full status in the tooltip.

## [1.1.14] - 2026-10-05

### Fixed
- Keep both sidebars and the canvas inside the window, including after shrinking the window or dragging an oversized sidebar. Release splitter mouse capture on mouse-up and focus loss; contain toolbar tabs within their own scroll area.
- Replace the Remesh preview stage dropdown with Source, Remesh, Simplify and Result buttons showing selection, ready/stale states and active pipeline progress.

## [1.1.13] - 2026-10-05

### Fixed
- Fit coarse Simplify vertices to the original source using their full one-ring error, then regularize poor triangle pairs with a bounded sub-voxel shape budget. Preserve boundaries, source folds and bidirectional surface-error guards; rebuild normals/UVs after the changed geometry.

## [1.1.12] - 2026-10-05

### Fixed
- Remesh & Bake preserves the 3D camera's pan, orbit and zoom when switching Source, Remesh, Simplified and Result stages or the trim-mask display. Automatic framing follows the selected source object; explicit Frame still fits the current stage.

## [1.1.11] - 2026-10-05

### Fixed
- Simplify compares both diagonals of coarse organic patches against the captured source surface after vertex fitting. Curvature can drive a diagonal change without requiring better triangle shape; the final pass preserves vertex positions, face count, boundaries and component topology, and retains the sampled surface-error gates.

## [1.1.10] - 2026-10-05

### Fixed
- Merged-chart tangents rebuild from final UVs on small models instead of retaining the previous atlas frames. Copies of a removed seam share tangent accumulations when their final position, normal, UV and chart match; UV and smoothing discontinuities remain independent.
- Surface padding continues the receiving chart's tangent frame beyond its UV boundary. Normal detail crosses smooth physical edges without an artificial geometric-frame twist; singular or reversed frame continuations fall back to their reachable boundary frame.

## [1.1.9] - 2026-10-05

### Fixed
- GPU BVH filters guard optional normal and either-side buffer loads explicitly rather than relying on HLSL logical operators to short-circuit. Unfiltered queries and absent masks remain safe on OpenGL/Vulkan as well as Direct3D.
- Vertex AO verifies both compute kernels on the active graphics API before uploading geometry, allowing the existing CPU fallback to handle unsupported kernels.

### Added
- Graphics API validation covers actual DX11, DX12, OpenGL Core and Vulkan execution, both XYZ/DXT5nm normal encodings, and Android GLES3/Vulkan shader bundle compilation. See `Documentation~/GRAPHICS_API_VALIDATION.md` for the checks and device-validation limits.

## [1.1.8] - 2026-10-05

### Fixed
- GPU BVH, source AO and vertex AO compute shaders resolve their shared traversal include through the canonical UPM package path. This avoids relying on sibling include lookup during package imports.
- Shared GPU intersection and nearest-point helpers return initialized results through a single exit, avoiding uninitialized-value diagnostics during Android/Vulkan compilation while preserving intersection predicates and hit ranking.

## [1.1.7] - 2026-10-05

### Fixed
- Mandatory UV overlap repair waits for an occupied xatlas repack session with cancellation support instead of treating temporary contention as packing failure. Cancelling a waiter preserves the current owner's session; optional repack calls retain their busy rejection.

## [1.1.6] - 2026-10-05

### Fixed
- Remesh Result preview packs normal maps for the active Lit decoder, eliminating chart-shaped shading patches with Android DXT5nm encoding. Maps and exported PNG bytes remain unchanged.
- GPU bake uses larger bounded asynchronous query batches to reduce Editor scheduling overhead, reports stage timings, and compiles without the BVH traversal initialization warning.

## [1.1.4] - 2026-10-03

### Fixed
- 2D and 3D shell previews use the selected UV channel for matching colors; cached colors remain correct when switching channels and returning to an earlier channel.
- Texture AO uses the viewport floor orientation, covers thin UV triangles, offers CPU/GPU baking and exports RLE TGA. Remesh prepares wires, cages and GPU readbacks without blocking repaint; all baked maps receive a separate dilation pass after padding. UV generation compares bounded alternatives to reduce fragments, while intermediate normals/UV0 remain complete and draft UV0 is rejected until final normals and baking can use the real atlas.

## [1.1.0] - 2026-10-02

### Changed
- **CI: the Sonar workflow removes orphan throwaway projects, and Auto Version Bump respects a release bump.** A run cancelled between its scan and its delete step left its per-PR throwaway project on the server; every run now lists the throwaway projects of this repository (`Tools~/sonar-pr-check.mjs throwaways`) and deletes those whose GitHub run is over, keeping the ones still running. The patch auto-bump on `main` skips a push that already changed `package.json`'s version (a release cut in the branch), instead of bumping it a second time.
- **Diagnostics are a library and a tab: `Editor/Diagnostics/`, Diagnostics.** The debug surfaces were scattered: the Show Debug UI setting was read in seven places, the DEBUG banner and the log-filter block were drawn inline in the UV2 Transfer tab next to the sweep and benchmark buttons, the Remesh & Bake health check (cage welding, one-sided normals, fallbacks, scale ratio, normal-map tilt) was formatted and logged in one pipeline method so nothing else could read its judgement, and `HierarchicalDiag` / `FbxMetricsExporter` sat loose at the editor root. `DebugUi` is the one gate (`Enabled`), banner and log-filter block, and `IUvToolDebugOnly` marks a tab the hub hides while the setting is off; `BakeHealth` builds the node's health as a report (summary, scale ratio, heavy-reduction flag, warnings) that the pipeline logs and a test or the Diagnostics tab can read; the two diagnostic exporters move into the folder. The new **Diagnostics** tab (Show Debug UI on) hosts the parameter sweep, the multi-model benchmark and the report rebuild (through the UV2 Transfer tab's `IBenchmarkHost`), the log filters, the hierarchical probe of the current LODGroup, both FBX metrics exports and a button to the BenchmarkReports folder; the UV2 Transfer tab's debug section keeps UV0 Analysis & Fix only, since it edits that tab's working meshes. Behaviour unchanged: same warnings and thresholds, same artefacts. Tests pin the health report's summary, the scale tolerance, the zero-normal / fallback precedence and the tilt rule on mild, heavy and proxy reductions.
- **The benchmark is a library: `Editor/Bench/`.** The parameter sweep and the multi-model benchmark lived as 800 lines of orchestration in the UV2 Transfer tab (matrix validation and cell count, the seven nested axis loops with the context snapshotted and restored, per-cell labels and recorder routing, the incremental aggregate, manifest and archive, the per-case spawn, LODGroup lookup, technique dispatch and cleanup), so no other tool could run one. `SweepRunner` (matrix → `Axes` → `Cell`s with their labels and configs, `TryValidate`, `Run` through an `ISweepHost`) and `BenchmarkRunner` (`Run` through an `IBenchmarkHost`, `FindLodGroup`, `RebuildReport`) now hold that; the tab implements the two host interfaces (context, symmetry mode, reset working copies, run pipeline, bind a LODGroup) and keeps its three buttons. `BenchmarkRecorder`, `BenchmarkSweep`, `TestSuiteAsset` and `CsvUtil` move into the same folder (GUIDs unchanged, existing suite assets stay bound). Behaviour unchanged: same cell order, labels, directories, reports and cleanup. Tests pin the axis fallbacks, the cell count and order, the label format, the oversample clamp and the validation errors.
- **One texture writer: `TextureAssets`.** A transient `Texture2D` was created, filled, encoded and destroyed by hand in the remesh exporter (PNG and EXR maps), the remesh previews (map textures, the base colour preview), the hierarchical repack diagnostics (PNG write, PNG read-back) and the UV PNG writer, and the map importer setup sat in the remesh exporter. `Editor/Assets/TextureAssets.cs` is the one implementation (`FromPixels`, `EncodePng`, `EncodeExr`, `WriteFile` with the directory created, `WritePng`, `ReadImage`, `Configure` with a `Kind` of Color / Linear / NormalMap); every site calls it, the UV PNG writer destroys its read-back texture on a failed encode too, and the repack's backdrop load no longer leaves a texture behind. Tests pin the PNG round trip through `WritePng` / `ReadImage`, the linear flag of `FromPixels`, the EXR encode and the size guards.
- **One LODGroup-from-hierarchy: `LodHierarchy`.** Rebuilding a LODGroup from `_LOD{n}` names existed twice (Cleanup over direct children with gaps kept and halving transitions, Prefab Builder recursive and compact with linear transitions), creating a group from renderers twice (Cleanup at 0.5, LOD Generation at 0.01) and from detected siblings once, compacting a LOD array lived on the tool context, and moving a root mesh into a LOD0 child, the "root renderer is LOD0" check and the root MeshCollider from a collision child were Cleanup privates with the Prefab Builder's own collider assignment next to them. `Editor/Mesh/LodHierarchy.cs` is the one implementation (`CollectByName`, `LodsFromNames` with a `Transitions` scheme, `RebuildFromNames`, `CreateFromRenderers`, `CreateFromSiblings`, `Compact`, `RootRendererIsLod0`, `MoveRootMeshToChild` + `SortChildrenAsLods`, `AddColliderFromCollisionMesh`, `AssignCollider`); each tab keeps its policy (which children, which transitions) as arguments, `UvToolContext.CompactLodArray` and the LOD Generation statics forward, and the root-mesh move carries the full renderer settings through `RendererSettings` (it copied five of them). `LodGroupUtility` stays the component-level half. Creating a group on a root that already has a LODGroup reuses that component (with Undo) instead of dereferencing the null `AddComponent` returns for a duplicate. Tests pin the name collection (direct vs recursive, collision skipped), both slot layouts and transition schemes, the rebuild on a scene group, the root-is-LOD0 check, the root-mesh move with the polycount order, the collider from a collision child and the compaction.
- **One scalar vertex channel: `VertexChannels`.** The vertex AO target channel (a colour component or a UV component) was decoded by hand in the Vertex Colors tab (read, write, the submesh merge, the preview greys, the channel name from UI tables), in the AO baker (write), in the transfer tool's export (the AO UV channel) and in the sidecar store (the AO target), and the variant export painted, snapshotted and restored `colors32` on its own. `Editor/Mesh/VertexChannels.cs` is the one implementation (`IsColor`, `ColorComponent`, `UvChannel`, `UvComponent`, `Name`, `Read`, `Write`, `WriteSubmesh`, `Levels`, `Blend`, `Greyscale`, `FillColors`, `SnapshotColors`, `RestoreColors`); `VertexAOBaker.WriteToChannel` forwards, the tab keeps its blur pipeline and UI, and the submesh-only apply reports an out-of-range submesh the same way. The submesh-only apply now leaves every vertex outside the submesh byte- or float-exact (it used to read the whole channel back clamped to [0,1] and rewrite it). Tests pin the decoding of every channel, the read/write round trip on colours and UVs with the other components kept, the submesh write, levels and blend, and the snapshot/restore of colours.
- **One UV layout topology: `UvTopology`.** The UV canvas held the channel read, the boundary-edge count, point-in-triangle, the shell vote and the shell data build as public statics on the view; the shell extractor counted boundary edges again for its descriptors (same edge key, same loop), the face→shell cache in `MeshEntry.cs` rebuilt the map inline, and the 3D viewport had its own unique-edge dedupe for wires. `Editor/Uv/UvTopology.cs` is the one implementation (`ReadUv`, `HasUv`, `BoundaryEdgePairs` over all faces or a face subset, `BoundaryLength`, `UniqueEdges`, `PointInTriangle`, `VoteBestShell`, `FaceToShell`, `BuildShellData`, `OccupiedTiles`); the canvas keeps only its per-frame caches and the GL drawing, `FaceToShellCache` and `MeshViewport3D.EdgeIndices` call in, and the shell extractor's boundary length is the same count the canvas draws. Tests pin the boundary and unique edge sets of a two-island layout, the subset cut edge, the boundary length, point-in-triangle on edges and vertices in either winding, the shell vote, the face→shell map and shell bounds.
- **One FBX writer and one sidecar store: `FbxExport`, `SidecarStore`, `RendererSettings`.** The transfer tool carried the whole FBX pipeline (about 2,400 lines: the export mesh of an entry, the UV-channel carry-over, the isolated channel re-save with its snapshots, importer scope and atomic write, the LOD-rebuild passes, the collision injection and strip, the relink after reimport, the sidecar reads and writes) as private methods on the tab, and the other tabs reached it through `FindTool<LightmapTransferTool>()`; the sidecar asset was opened by hand in six places (load-or-create, `Set`, `SetDirty`, `SaveAssets`), the "FBX paths behind these entries" loop was written five times, the collision-hull loader lived on the Collision tab, and the renderer-settings copy existed three times (the split/merge one lost `scaleInLightmap` and the lightmap scale/offset). `Editor/Assets/FbxExport.cs` now holds the mechanics (`BuildExportMesh`, `WriteChannels`, `Write`, `WriteAtomic`, `PromoteRootMeshToLod0Child`, `ReplaceMeshes`, `AddLodChild`, `PruneStaleChildren`, `NormalizeExportHierarchy`, `InjectCollisionMeshes`, `StripCollisionMeshes`, `TrimMaterialArrays`, `RelinkSceneMeshReferences`, `RemoveDefaultMaterialRemaps`, the preflight and the UV channel helpers), `Editor/Assets/SidecarStore.cs` the sidecar (`Load`/`LoadOrCreate`/`Save`, `FbxPaths`, `TryFindFirst`, `SaveEntries`, `StoreForImport`, `ArmTransientReplay`, `ClearUv2Entries`, `Delete`, `TryBuildEntry`, `CollisionMeshes` with its validation, `LoadSettings`/`SaveSettings`), and `Editor/Mesh/RendererSettings.cs` the one renderer copy. The transfer tool keeps the policy only — dialogs, backups, the importer lock, which entries, what to refresh — and its wide export reads as the sequence of those passes; the hub, the Collision tab, vertex colour baking, LOD generation, split/merge and the remesh exporter call the libraries directly (`CollisionMeshTool.GetCollisionMeshesFromSidecar` forwards). One behaviour change: the wide LOD-rebuild export now rejects an empty exporter output as a failure, as the isolated path and the remesh exporter always did, instead of reporting success and restoring the `.meta` over it. Tests cover the naming rules, the UV carry-over, the AO target mapping, the hierarchy passes on a scratch hierarchy and the collision entry validation.
- **One way to read a mesh, one way to bake a matrix, one box math: `MeshAccess`, `MeshTransform`, `MeshGeometry`.** Reading a Read/Write-disabled mesh was done five ways: the UV canvas carried the only correct one (every attribute through `AcquireReadOnlyMeshData`, an importer round-trip only when the engine refuses even that), vertex AO instantiated the mesh, the Cleanup tab flipped the FBX importer's Read/Write on and off around its reads (two reimports per fix, with the postprocessor bypassed by hand), the rest just skipped unreadable meshes. `Editor/Mesh/MeshAccess.cs` now owns the canvas's implementation (`ReadableCopy`, `Readable`); the transfer tool, the remesh capture, vertex AO and both Cleanup fixes read through it, and no tool touches an importer to read a mesh any more. Baking a transform into a mesh existed three times (`LightmapTransferTool`, `PrefabBuilderTool`, `RemeshExporter`) and two of them transformed normals with the matrix itself, which is wrong under non-uniform scale and never flipped the winding of a mirrored mesh; `MeshTransform.BakeMatrix` does it once, correctly (inverse-transpose normals, re-orthogonalised tangents, winding and handedness flipped under a negative determinant), and `WorldBounds` / `CombinedBounds` replace the per-tool unions. The pure box math (`TransformBounds`, `BoundsDistance`) joins `MeshGeometry`, where the transfer, vertex-colour-baking and AO copies forward to it. The readable copy keeps every submesh at its own topology (quads and lines were triangulated), keeps an all-zero UV channel, reads colours as floats (a `Color32` read quantised float colour streams), and warns once when a skinned Read/Write-disabled mesh goes through `MeshData`, which has no bone-weight accessor; a mirroring `BakeMatrix` re-winds quads as quads.
- **One split-by-material / merge-same-material: `MeshSplitMerge`.** The Cleanup and Prefab Builder tabs each carried their own scan, split and merge (about 450 and 260 lines) and they had drifted: the Prefab Builder merge kept only positions, normals and UV0 (tangents, colours and UV2 were lost), its split read the materials while its own preview had them swapped, and the Cleanup split dropped the static flags. `Editor/Mesh/MeshSplitMerge.cs` is the one implementation: `Scan` (candidates and groups), `SplitByMaterial` (one child per submesh with every attribute, the renderer settings and static flags, slotted into the LODGroup where the source was), `MergeSameMaterial` (into the first renderer, which keeps its components and references and is renamed `{base}_LOD{n}`, every attribute carried over, the others leave the LODGroup), `ExtractSubmesh` (all eight UV channels at their stored width, bone weights included; `MeshHygieneUtility.ExtractSubmesh` forwards to it). Both tabs keep only their buttons and lists; previews are restored before materials are read; Undo and prefab-instance modifications are recorded in one place. A merge whose first renderer is not readable is skipped instead of replacing that renderer's geometry with the others' alone; a split whose submeshes are all empty leaves the object alone; a split source that carries children or other components stays as their container and only loses its mesh pair; a mirrored part (negative-determinant transform) is re-wound and its tangent handedness flipped when merged, and part normals go through the inverse transpose.
- **One reader of the naming rules: `MeshNaming`.** LOD and collision suffixes were parsed by nine different regexes — `UvToolContext.ExtractGroupKey`, three in `MeshHygieneUtility`, two in `LodPipelineOps`, three inline in `LodGenerationTool`, five inline in `CleanupTool`, three in `PrefabBuilderTool`, a hand-rolled `_COL`/`_COL_Hull{N}` pair in `VertexColorBakingTool`, a `LastIndexOf("_LOD")` scan in `RemeshSource` and two more in `LightmapTransferTool` — and they had drifted: some required `_LOD`, some accepted `-LOD` and ` LOD`, one accepted `FooLOD1` with no separator, the collision rules disagreed on `_COL_*` and `_Collider`. `Editor/Mesh/MeshNaming.cs` is now the one implementation (`TryParseLod` with the LODGroup index limit, `LodIndex`, `HasLodSuffix`, `StripLod`, `SplitLodSuffix` keeping the suffix text verbatim for renames, `LodName`, `IsCollision`, `StripCollision`, `HasLodOrCollisionSuffix`, `GroupKey`, `StripPipelineSuffixes`); every site calls it, `ExtractGroupKey` and the `MeshHygieneUtility` helpers forward to it, and a test file pins the accepted forms. The LOD separator is `_`, `-` or whitespace everywhere now (a bare `FooLOD1` is no longer a LOD name anywhere), and the collision rule is the hygiene one everywhere (`_COL`, `_COL_Hull{N}`, `_COL_*`, `_Collider`, `_Collision`; `_COLOR` is not).
- **One geometry module for every projecting and baking tool.** An audit of the BVH and GPU paths found no duplicate BVH (one 3D `TriangleBvh`, one UV-space `TriangleBvh2D`, the GPU AO kernel consuming the 3D one's layout) but a ring of tool-local copies around them: face normals computed in five places, bit-exact position welding in four, the golden-spiral sample directions in two, 2D barycentrics in two, a point–box distance and a closest-point-on-triangle duplicated in `HierarchicalRepack` for a brute-force O(N)-per-query nearest-face scan, and the "blit to a temporary render texture, ReadPixels, clean up" GPU readback written out three times (source maps, lightmap regions, probe cubemaps). They now live in `Editor/Geometry/`: `MeshGeometry` (face normals, `WeldPositions`, `SphereDirections`, `Barycentric`, `SqDistToAabb`) and `GpuReadback` (`Read` / `ReadColors`), used by the remesh bake, trim and cage, the Beauty capture, the source capture, vertex AO (CPU and GPU), the edge analyzer, the UV canvas and the hierarchical repack, whose nearest-face projection runs on the BVH now (O(log N)). Two dead shaders from the abandoned depth-based AO path (`VertexAOAccum.compute`, `VertexAODepth.shader`) are removed; nothing referenced them.
- **Remesh & Bake: the result's atlas lives in the canvas, the right panel is 3D + Maps.** The right sidebar's own UV view (a square with tinted islands over the base color) is gone; the canvas's UV mode now shows the Remesh & Bake result once Normals & UV ran, with the same shells, wire, border, spot picking, status line and backgrounds as any mesh (the baked base color under it, or the checker), and an **Islands** fill mode tints every UV shell. The right panel is a vertical list fit for the narrow third column — Stage, Baked base color, Baked normal map, Trim mask, Cage shells, the atlas's island count and texel usage — and the Maps view; its own Wire, Shaded and Vertex color toggles are gone because the canvas owns them: the status bar's Wire now draws on tool content too, the 3D shading row's modes apply to the stage mesh, and the result's UV layer (fill mode, island borders, spot picking) is drawn on the model in 3D as for any context mesh (the hub pairs a tool's 3D items with its UV entries by mesh). Tools opt into this through `IUvToolUvContent` (the hub sets `UvCanvasView.EntriesOverride` each frame); a `MeshEntry.previewTexture` carries a tool-made entry's background in the UV canvas and is skipped by the 3D layer, which would only dim the surface already showing it.
- **Remesh & Bake never touches the source importers.** The remesh stage used to flip `isReadable` on for every imported model under the source root and back off afterwards — two reimports of every FBX per run, which in projects with heavy model postprocessors (Bakery's UV-overlap check) cost more than the capture itself. Read/Write-disabled meshes are read through `Mesh.AcquireReadOnlyMeshData` regardless, so the flip is gone; skinned sources bake through `BakeMesh` as before.
- **Remesh & Bake: the tool is split into a stage pipeline, an exporter and the tab.** `RemeshBakeTool` (1000+ lines mixing IMGUI, two copies of every stage for the weld and keep-hierarchy lanes, and two near-identical save paths) is now the tab only. `RemeshPipeline` runs the stages over a list of nodes — the weld is one node in root space, keep-hierarchy one node per renderer — with a single loop per stage, one `ClearFrom`, and previews drawn from the primary (largest) node; `RemeshExporter` writes maps, materials, meshes and the prefab/FBX through one path for both lanes; `RemeshSource.CollectRenderers` is the one renderer filter (the weld capture and the node list agree by construction). `RemeshBeauty` keeps its scene data in world space and converts per capture space (`ForSpace`), so one snapshot serves every node. No settings, ABI or asset formats changed.

### Fixed
- **Beauty bake: scene lighting matches the game in three more cases.** Under Subtractive mixed lighting a lightmapped receiver no longer adds the mixed lights' realtime direct term on top of the lightmap that already carries it; a shadow caster on a layer a light's culling mask excludes no longer shadows that light (the ray steps past it, for the source's own faces and the scene casters alike); and a scene caster without UV0 (procedural or imported geometry) stays in the shadow BVH instead of being dropped by the material-transfer UV check.
- **Beauty results in URP save with `Universal Render Pipeline/Unlit`** (`_BaseMap`) instead of the Built-in `Unlit/Texture`, which renders pink there.
- **3D canvas: the shading row offers Normals and Tangents only where a mesh carries them**, as it already did for UV channels, and a mesh whose colours or UVs a tool writes (Vertex Colors, AO) re-encodes on the next frame instead of showing the pre-edit clone.
- **Variant export restores a mesh that had no vertex colours to having none** rather than leaving a full-length black stream behind.
- **`UvCanvasView` keeps `VoteBestShell`, `BuildBoundaryEdgePairs`, `PointInTriangle` and `MakeReadableCopy` as obsolete public forwarders** to `UvTopology` / `MeshAccess`, so code compiled against them keeps building.
- **BVH rays are watertight and the tree is SAH-built.** Two findings from a read of the open implementations (tinybvh, the Woop–Benthin–Wald JCGT 2013 paper). The ray–triangle test was Möller–Trumbore, which can leak a ray between two adjacent triangles exactly along their shared edge or through a shared vertex — on a bake that is a missed texel for every seam a ray happens to graze. The test is now the watertight one: vertices translated to the ray origin and sheared so the ray becomes (0,0,1), three 2D edge functions with an exact-zero edge re-evaluated in double precision and counted as inside, two-sided with the determinant's sign folded into the depth test; the same test in `BvhTraversal.hlsl` (conservative without the double fallback on GPUs). The build was a midpoint split of the longest axis with 4-triangle leaves; it is now a binned SAH build (8 bins on each axis over the centroid bounds, cheapest plane by surface-area heuristic, a split only when it beats testing the node, leaves capped at 8 with a midpoint fallback for coincident centroids), which visits fewer nodes per ray and per nearest query on the same geometry — the GPU kernels traverse the same tree. Tests cast rays through every shared vertex and edge of a grid and compare the tree's answers with an exhaustive scan over a random soup.
- **BVH rays hit millimetre-scale meshes.** The ray–triangle test rejected a triangle as "parallel" when its Möller–Trumbore determinant was under an absolute 1e-7, but that determinant scales with the product of two edge lengths, so every face of a small model (edges around 1e-4 units) failed the test and the bake, the trim's reach and vertex AO saw nothing. The threshold is relative to the triangle's own scale now, on the CPU BVH and in the GPU AO kernel alike; a regression test casts at a 1e-4 triangle.
- **Remesh & Bake: Trim to source surface now actually removes the back of an open sheet.** The trim judged a remesh face by position ("is its centroid behind the source sheet?"), but the voxel remesher fits its output vertices onto the input surface, so the slab around an open wall is not a cell thick: its front and back faces lie ON the sheet, both at zero distance, and both passed — a zero-thickness double-sided result on every wall of a non-closed source (half the positions of a bathroom interior carried two sides), which then broke the projection cage and baked texels from the wrong side. The trim now keeps a face only when a source face within two cells points the same way: the back (opposite normal) and the rims (perpendicular) go, a closed or genuinely double-sided source keeps both sides, an inside-out source is judged by its flipped normals, and when neither reading keeps a tenth of the remesh the stage warns instead of silently keeping everything. On a one-sided open room the trim removes 50% of the remesh (back and rims) where it removed 3% before.
- **Remesh & Bake, keep hierarchy: nothing is captured twice, sheared transforms survive, and previews/exports are coherent.** A node's capture folded its whole subtree in while every child renderer was also its own node, so nested renderers baked into two meshes; each renderer is now exactly one node. A node's capture space is the TRS the save restores (root × TRS), so a rotated child under a non-uniformly scaled parent folds the residual shear into its geometry instead of losing it. The node list applies the same mesh-asset name filters (`_COL`, `_LOD{n}`) as the weld, so a normally named object carrying a collision mesh no longer aborts the whole run. Re-running Normals & UV destroys the previous node meshes (each iteration leaked one `HideAndDontSave` mesh per node); a re-bake with vertex color transfer off clears the colors the previous bake left on every node; the UV and cage previews show the largest node's geometry instead of "run the UV stage"; and the hierarchy save imports the map files before configuring their importers (one `AssetEditingScope` deferred the imports past the importer lookup, which could fail and roll the whole folder back).
- **Remesh & Bake: Beauty bakes shade lightmapped faces as the game does.** The lightmap was written out as the colour itself; Unity lightmaps store irradiance that the Lit shader multiplies by albedo, so textured lightmapped objects baked as flat light. Lightmapped faces are now albedo × (lightmap + realtime direct) + emission. Point and spot lights respect their shadow setting and strength (they cast opaque shadows regardless before), light ranges and shadow distances are evaluated in world units (a root scaled to 0.01 or 100 no longer stretches or shrinks their reach), and keep-hierarchy nodes evaluate the lighting in their own space instead of the root's.
- **Remesh & Bake: *Smooth* and *Angle* hard-edge modes are smooth across UV islands again.** The post-unwrap normal regeneration accumulated per output vertex, and xatlas splits vertices along every chart border, so every mode hardened island borders — *Smooth* behaved like *UV islands*, *Angle* like *UV islands + angle*. Copies along chart borders are now grouped back by position and native crease group, and normal smoothing runs over the same groups. Tangents are re-orthogonalized against the final normals so the saved frame matches the bake's. The front-face-filtered nearest fallback is bounded by the projection distance like the unfiltered one (it could sample an arbitrarily distant part across a gap).
- **Remesh & Bake, normalized saves: bounds and tangents.** Baking the source scale into the mesh left `mesh.bounds` at the unscaled size (culling at the wrong extent) and scaled tangents by the inverse like normals — tangents are direction vectors and take the forward scale, then re-orthogonalize; a mirroring scale also flips the tangent handedness with the winding. Skinned-only roots now follow the hierarchy selection like mesh roots. The saved model's scale is the one captured at remesh time, not whatever is selected at save time.
- **Remesh & Bake no longer reads whole scene lightmaps at capture.** The capture read every lightmap a renderer referenced in full, as float pixels (16 bytes per texel, 1 GiB for an 8K map, plus the same again as a RenderTexture) — in every bake mode — which failed with "GetPixels: array size too large" on large Bakery lightmaps and drove the GPU out of memory (D3D `8007000e`, crashing the asset import worker on the following reimport). The capture now keeps only the texture references; a Beauty bake reads, right before it runs, just the region each renderer occupies (its scale/offset rect plus one texel of padding, downsampled above 2048² texels) and releases the pixels when the bake ends. Materials bakes never touch the lightmaps.
- **FBX isolated export restores the source importer on every exit.** The variant `keepQuads` toggle and the re-save `isReadable` flip were put back only in Phase 5 (and the toggle also in the catch), so the early "no matching meshes" return and a failure after Phase 3 left the source importer changed. Both are now restored by a scope disposed at method exit.
- **Sweep gallery pages are reachable and never overwrite each other.** The index and per-page navigation linked `_gallery_<raw name>.html` while the page was written under the sanitized name (a model named `A B` had no working link), and two models sanitizing to the same string wrote the same file. Every link now uses the written name, and colliding names get a stable `_2`, `_3`… suffix in model order.
- **Repository: a stray screenshot committed at the package root is removed.**

### Added
- **CI: the whole package compiles without a Unity licence, and a SonarQube scan.** `Tools~/compile_check.py` builds `Editor/` and `Tests/` with the .NET SDK against the UnityEngine/UnityEditor reference assemblies from NuGet (Unity3D.SDK 2021.1.14.1), once without and once with `LIGHTMAP_UV_TOOL_FBX_EXPORTER` (a stub stands in for the FBX Exporter), and fails on any CS error with the repo-relative file and line; the four APIs the 2021 references predate are bridged on copied sources, never the repo. The *Unity Package Checks* workflow runs it on every push and PR next to the CS0103 heuristic. A *SonarQube* workflow set, modelled on SashaRX/Space, analyses the same build with `dotnet-sonarscanner` on the self-hosted server (`SONAR_TOKEN` secret; host and project key as repository variables) and skips itself with a notice while the token is missing: a push to `main` updates the project itself with the quality gate enforced; every pull request is scanned into a per-run throwaway project and `Tools~/sonar-pr-check.mjs` keeps the open issues on the lines the PR changes (annotations, job summary, a findings artifact, the project deleted afterwards; any new finding reds the check), so the Community edition's one-branch-per-project limit still yields a per-PR verdict; each check also uploads a findings bundle (`findings.json`, a fixer prompt) as an artifact; `sonar-backlog-report.yml` is the read-only triage snapshot and `Tools~/sonar-mcp.sh` the read-only SonarQube MCP server for interactive sessions. Four rules are switched off in configuration with their reasons (S125 prose comments, S1104 public data fields, S107 scalar math parameters, S1168 null as "absent") and three are advisory, reported but not gating (S3776 cognitive complexity, S3267 LINQ-over-loop, S3358 nested ternary, S1075 hardcoded path delimiter on Unity asset paths). A PR that changes the Sonar tooling checks every open PR with it; `workflow_dispatch` takes `open`, PR numbers or `main`. Runbook: `the Sonar runbook in SashaRX/Space (docs/sonar-autofix.md; here the tools live in Tools~/ and the config in ~/.config/meshlab/)`.
- **The GPU BVH does everything the CPU one does, and the bake uses it.** The only GPU tree so far was the vertex-AO kernel's private copy, which answered just "how far until a hit". `Shaders/BvhTraversal.hlsl` is now the one traversal for every compute kernel — the same node layout as `TriangleBvh`, the same relative ray–triangle test, closest-point-on-triangle with barycentrics, ordered (nearer child first) ray traversal, nearest with box-distance pruning, the facing filter, the normal filter and the either-side mask — and `Shaders/BvhQueries.compute` answers batches of ray and nearest-point queries with the same hit records the CPU returns (face index, t or distance², weights of vertices 0, 1, 2). `GpuBvh` (C#) uploads a `TriangleBvh` once and streams batches through it, or binds the tree to another kernel; the AO kernel now includes the shared traversal and binds the tree through it instead of carrying its own. The Remesh & Bake bake is restructured into one code path with a pluggable resolver: Prepare (coverage, BVH, cage, orientation probe), then bands of rows — sample requests built on workers, answered either by the CPU BVH in parallel or by the GPU in one dispatch per batch, evaluated on workers — then the fills, diagnostics, dilation and vertex colours. **GPU projection** (bake stage, default on where compute shaders exist) picks the GPU resolver; the pipeline drives the band loop from the main thread so the dispatches are legal, falls back to the CPU when the kernel asset is missing, and the bake status says which path ran. Results are the CPU's.
- **Remesh & Bake: two-sided sources, a visible trim mask, and a re-wound sheet.** A material that renders both sides (Cull Off, double-sided) shows its sheet from behind too. **Source backfaces** (remesh stage) decides which source faces count from behind: *From materials* (default) reads each captured material's cull mode property (`_Cull`, `_CullMode` = Off) or double-sided switch (`_DoubleSidedEnable`, `_TwoSided`, `_DoubleSided`), *Always* treats every face as two-sided (for a `Cull Off` baked into the shader, which no property reveals), *Never* keeps only fronts. A two-sided source still trims to ONE sheet — doubling the geometry would double the atlas and the triangle count for nothing — and the saved material renders both sides of it instead (URP Lit/Unlit: cull Off and double-sided GI; Standard and Unlit/Texture have no two-sided mode, so the save warns and says so in its status); two-sided faces pass the bake's front-face filter from either side (ray and nearest fallback alike) and cast no vote in the winding probe. After the cut the kept sheet is re-wound to one consistent orientation per connected piece, the majority of each piece keeping the side that agreed with the source, so a source modeled with arbitrary winding comes out orientable. The Remesh stage's new **Trim** toggle (3D panel) colours the untrimmed remesh by class — green kept, red back of a sheet, orange rim / no source within reach — so what the trim removes, and why, is visible before the simplifier runs; the remesh status now also reaches the Console, with the trimmed, re-wound and two-sided counts, and RemeshDiag prints the per-class breakdown.
- **Remesh & Bake: a sided, fitted projection cage.** The cage was one direction per welded position: the area-weighted face normals of every vertex copy at that position, summed and smoothed. On a double-sided sheet — a wall of a non-closed source that the voxel remesh turned into a one-cell slab and the simplifier collapsed to zero thickness, both windings on the same vertices — the two sides summed to nothing and the normalized noise sent rays off at up to 180° from their face, with the cage preview spiking across the whole model and most texels baking from the wrong side. The cage is now built per face corner and per **side**: corners at one position are clustered by the hemisphere their face normals share (within 120°), each cluster is averaged (face area × corner angle) and Laplacian-smoothed over its own connectivity, so UV and crease splits still weld into one smooth direction while a double-sided sheet keeps a front and a back; every corner direction is checked against its own face and falls back to the unsmoothed side, then the face normal. **Cage smoothing** (bake stage, 0–10, default 2) exposes the passes. **Fit cage to source** (default on) measures where the source sits along each side's ray (both ways, then nearest point), doubles it for oblique surfaces, clamps it between 1× and 8× the projection distance and smooths it over the sides, so the rays reach as far as the decimation drifted and no further; the nearest fallback is bounded by the same reach. The Cage preview pushes every corner by its own reach, one line per welded side pair (a double-sided sheet shows both shells), rebuilt live from distance, smoothing, fit and source. RemeshDiag reports the side and double-sided position counts and the longest reach; the bake staleness key covers the new settings.
- **A shared 3D view in the canvas, for every tab.** The canvas gains a **UV | 3D** switch: a segmented pill at the bottom centre with the active side in the accent colour, present in every canvas state (also when the UV canvas has nothing to draw), remembered across sessions, and flipped with **Tab** while the mouse is over the canvas. The 3D side looks like a scene view: a blue-grey background, a fading ground grid under the content (Grid toggle), the pivot's axes, and the shading modes as a segmented row along the top of the canvas. The 3D side is one module, `MeshViewport3D`: an orbitable lit view (drag orbits, middle button or alt-drag pans, scroll zooms, F or a middle double-click frames) with shading modes *Shaded* (scene materials), *Vert Colors*, *Normals*, *Tangents* and *UV0–UV3* (channels offered only where a mesh carries them), a wire overlay and a Lit toggle. By default it shows the preview LOD's meshes exactly as the UV canvas lists them (isolated mesh group included). Tools take part through `IUvTool3D`: they can replace the content and draw overlays (meshes, wire, coloured lines, screen-sized dots). The 3D side is the UV viewer on the model: the same bottom bar (fill mode and alpha, Wire, Bdr, preview mode with checker channels and lightmap exposure) and the same Spot / Lock / Clear and UV0 / UV1 controls drive it — the active fill mode, borders and preview background are rendered per mesh into a UV-space layer and laid over the surface through the preview UV channel, Wire draws as true 3D lines, and Spot picks the face under the mouse (camera ray) into the same hover/selection state the UV canvas and the tools read, with the hovered and selected shells highlighted on the surface and the info panel in the corner. Remesh & Bake now shows its pipeline stage in that view — its right sidebar keeps the stage picker and the Wire / Shaded / Texture / Bump / Vertex color / Cage toggles, the small embedded preview is gone — and Prefab Builder's *Edges* and *Problems* scene previews paint their edge classes and unused-vertex dots there too.
- **Remesh & Bake: proxy shapes rebuilt — oriented boxes and hulls, a part filter before the remesh, vertex-color albedo tint.** The *Box set* shape (recursive gap splitting of a point cloud, which guessed wrong on anything that fills its bounds) is gone. **Shape** now offers *Bounding box* — one oriented box per captured renderer, measured over the renderer's geometry along its own authored axes and placed back in the capture space (a yawed building keeps a yawed box, not the inflated axis-aligned one; weld lane: one mesh of per-renderer boxes; keep-hierarchy: one box per node) — and *Hull* — the capture run through a coarse solid voxel pass (*Hull resolution*) and a strongly regularized simplification to *Hull triangles*, a closed rounded blob that follows L- and T-shapes. **Exclude parts smaller than** (a fraction of the capture diagonal) and **Exclude rods thinner than** (in voxel cells) run before any shape over the connected components of the position-welded triangle graph: a piece goes when its extent is under the size floor or when its cross-section — the two smaller of its extents along its own principal axes — is under the rod threshold, the section the voxel grid cannot carry anyway. Thin alone is not the criterion: a gate leaf or a glass pane has one thin extent and stays, a pipe or railing has two and goes, so rods stop inflating boxes and hulls; the status reports the counts and a filter that would remove everything is skipped with a warning. Proxy shapes bake through **proxy projection**: each texel casts along its face normal from just outside the proxy as deep as *Proxy search depth* allows (a fraction of the capture diagonal, 0.1 by default), takes the first source hit, else the nearest surface within the same reach — no cage — and texels that find no geometry within reach are written with alpha 0 (filled from the nearest hit so the color map stays clean), counted in the status, so an alpha-clipping shader gets a silhouette-correct impostor. **Vertex color tints albedo** (bake stage, on by default, inert on meshes without colors) multiplies the baked albedo by the source's interpolated vertex color RGB as linear, the way vertex-tinting shaders read it. Materials on shaders other than Standard / URP Lit are reported in one summary line per capture, grouped by shader, instead of one warning per material. The RemeshDiag "map is dominated by extreme normals" warning is skipped for proxy shapes and for reductions stronger than 5:1, where the geometry is meant to move into the normal map. **Highlight capture in Scene** (remesh stage) paints the source in the Scene view as the capture will see it, live with the filter sliders: green captured, orange small parts, red rods, grey excluded renderers (LOD1+, collision, disabled), with per-class triangle counts in the sidebar — a geometry-only capture, no material reads. The remesh and bake staleness keys cover the new settings.
- **Remesh & Bake: Keep hierarchy.** A *Keep hierarchy* toggle (remesh stage, default off) remeshes every captured node SEPARATELY instead of welding the subtree into one mesh: each renderer that the weld would have folded in (active, non-collision, LOD0 — the same filters, with its own descendants merged into its node) runs the full voxel → simplify → unwrap → bake chain in its own local space, and the save produces a hierarchy — one root prefab named after the source, one child per node at its captured root-relative transform, each with its own baked material and mesh asset (`<Node>_LOD0.asset` + `<Node>.mat` + the five map files). The FBX lane stays a single-mesh feature (a multi-node FBX round-trip re-imports per-node normals/mappings for no gain over the native prefab), so the FBX option applies to the weld only; the source root's scale stays on the prefab root, and *Normalized size* (a single-vertex-space concept) logs that it is weld-only. Changing the toggle invalidates the remesh stage through the staleness key like any other capture-scope setting.

### Fixed
- **One hostile mesh no longer kills the whole capture.** A non-triangle submesh (`WindowsCurtain_15`'s line geometry) or a mesh without UV0 threw and aborted the entire scene-block run. Non-triangle submeshes now drop out individually (the renderer survives on its triangle submeshes; a renderer with none left is skipped), UV0-less renderers skip with a warning, and every renderer is isolated so an unreadable import costs its own exclusion — never the capture.
- **Read/Write-disabled imports the MeshData path refuses are read through their importer.** Unity 6000.2's `AcquireReadOnlyMeshData` still throws `isReadable is false` for some imports. When it does, the read falls back to flipping THAT file's ModelImporter to Read/Write, reading, and restoring it — a reimport pair only for files the MeshData path cannot serve (typically none), never the flip-everything cost that was reverted before.
- **Collision meshes with suffixed names are excluded.** The collision matcher only accepted `Name_COL` and `Name_COL_Hull{N}` literally, so assets carrying variants like `Name_COL_M` (no UV0, rightly) aborted the whole capture with "needs source UV0". The matcher now takes the `_COL` token with any trailing suffix token (`_COL_M`, `_COL_S`, `_COL_Hull2`…) while ordinary words (`_COLOR`, `_COLLECTION`) stay out, and the capture checks the mesh asset's name for the suffix as well, not only the GameObject's.
- **The projection cage is a proper smooth cage.** The welded cage directions were raw area-weighted sums, so the sliver noise of the adaptive decimation went straight into them — the preview shells inflated lumpy and every tight concavity folded through to the far side. The directions are now Laplacian-smoothed over the welded connectivity (smoothing runs on the welded mesh so it flows through chart borders and hard edges instead of re-splitting them — the "fully smooth cage" bakers prescribe, applied to the bake rays too), and each preview shell's offset stops short of self-intersection by casting its segment against the surface, showing a pinch in wheel wells instead of noise.
- **Beauty bakes decode Bakery lightmaps correctly.** Unity's own RGBA32 lightmaps are RGBM (`rgb × a × 8`); Bakery's 8-bit output is plain linear with alpha pinned at 1, which the unconditional RGBM decode would overbrighten eightfold. HDR encodings (Bakery's default `.hdr`) always decoded directly; 8-bit maps are now auto-detected by the pinned alpha. Direction textures are not sampled (the colour map already carries each surface's irradiance) and shadowmask lights contribute through their realtime component with the bake's ray shadows standing in for the mask.
- **Remesh & Bake: the voxel remesh is trimmed to the source surface.** The voxelizer closes every surface, so an open source (a wall sheet, a roof plane, a curtain) came back as a thin slab — a front, a back and rims — and the back side baked and shipped as geometry. *Trim to source surface* (voxel remesh stage, default on) now masks the remesh after voxelization: a face stays only when a source face lies within two voxel cells of it, parallel to it (either winding), with the remesh face on that source face's front side; the slab's back and rims have none and go, closed sources are left whole, double-sided sources keep both sides, and a source wound inside out is judged by its flipped normals. The status reports the trimmed face count; the toggle is part of the remesh staleness key.
- **Remesh & Bake: proxy bakes are bounded and much faster; the bake reports its time.** A proxy texel looked through the whole proxy depth (the capture diagonal) with no fallback, so a 56k-triangle block under a hull cost a full BVH traversal for every texel that saw nothing and showed the far side of a courtyard through the block. The look is now bounded by *Proxy search depth* with a nearest-surface fallback within the same reach, the BVH visits the nearer child first so a hit culls the rest of a long ray, Beauty's scene shadow casters are limited to the source's surroundings (its bounds grown by twice their size), and the bake status ends with the elapsed seconds.
- **Remesh & Bake, Beauty: scene shadows, light culling masks, probe blending, skybox reflections.** Shadow rays saw only the source's own geometry, so a neighbouring building or a sibling keep-hierarchy node never shadowed it; the lighting snapshot now also captures the scene's shadow casters (geometry-only, nearest first, 4M-triangle budget) and tests them in world space. Lights whose culling mask excludes the captured renderer's layer no longer light or shadow it. Reflection probes are blended as the runtime blends them (weight 1 inside the box, 0 across the blend distance, two probes by importance, the environment taking the rest) instead of one hard-edged pick, and with Environment Reflections set to Skybox the generated skybox reflection is the fallback instead of black.
- **Remesh & Bake: a stage reports done only when every keep-hierarchy node has its output.** The stage check looked at the first node, so a repaint between two nodes of a Simplify, Unwrap or Bake dereferenced a node that had not run yet (an `OnGUI` null reference). A `Dispose()` during a run (tab switch, window close) cleared the node list under the still-running stage; the clear now waits for the run to observe its cancellation. A renderer that fails halfway through its capture (a missing material on a later submesh) is rolled back instead of leaving orphan vertices for the oriented boxes. The save resolves its output folder from the root the remesh stage captured, not from whatever is selected at save time. Unknown shaders keep each map's own tiling and offset instead of the base map's. The benchmark sweep drains git's asynchronous output before reading it.
- **Remesh & Bake: the front-face filter's orientation probe no longer depends on the target's density.** The probe compared source face normals with the target's cage normals at the nearest point, which splits its vote once the target is a coarse proxy (a 112-triangle garage block, a box, a hull) and switched the filter off exactly where thin walls matter most. It now judges from outside: 256 rays from a sphere around the source toward its centre meet an outer surface first, and that triangle's winding against the ray decides; the 70% majority rule and the off state for open sheets are unchanged.
- **Remesh & Bake: projection rays no longer sample through thin walls.** The bake raycast returned the closest intersection with no facing test, and the ray travels twice the projection distance through the target — on thin geometry (armor plates, fenders, any two-sided shell) it pierced the wall and baked the far side's texture as periodic mirrored/garbled patches. A front-face filter now only accepts source triangles facing the ray, on the ray path and on the nearest-fallback alike. Its orientation comes from a consensus probe of the source winding against the target's cage normals (inverted-winding imports are flipped automatically); without a clear ≥70% majority the filter stays off and the bake behaves exactly as before. The RemeshDiag projection line reports whether the filter engaged.
- **Remesh & Bake: Read/Write-disabled sources capture again.** `MakeReadableCopy` read the classic vertex getters, which on a non-readable import log "Not allowed to access" and return EMPTY arrays — the copy came out triangle-less and the capture aborted with "No source triangles found". Non-readable meshes now clone through `Mesh.AcquireReadOnlyMeshData` (vertices, normals, tangents, colors, UV channels, submeshes; bone weights have no MeshData accessor and stay a readable-only path). The readable fast path is unchanged.
- **Skinned captures bake the pose the scene shows.** `BakeMesh` uses the last evaluated skinning, which in edit mode can predate the current bone transforms — every skinned part then baked at its authored origin instead of its posed place. The bone list is reassigned before baking to mark the skinning dirty and force a re-evaluation.
- **Remesh & Bake: split-normal regeneration is scale-free and self-heals against the native normals.** The post-UV rebuild gated each vertex's accumulation behind an absolute floor (`sqrMagnitude > 1e-30`), which misclassifies valid smooth vertices on real ~5 mm captures (raw face crosses sit near 1e-7) and leaves their normals zero — every mode then baked through zeroed ray directions (tilt saturation, near-total projection fallbacks). The gate is now relative to the strongest accumulation on the mesh, and any vertex that still degenerates restores the parsed native meshopt normal (same crease splits, smooth across chart borders — a soft edge beats a dead one) with a warning naming the count. The native test suite gains a dense 5 mm sphere through the island-mode path (crease = π) pinning unit normals, normalized UVs and unit tangents at capture scale.
- **UvtLog no longer reads EditorPrefs from worker threads.** The level/category caches fill lazily, so the first remesh log emitted from a `Task.Run` worker could hit `EditorPrefs.GetInt` off the main thread and throw `UnityException`; both caches are now warmed in `[InitializeOnLoadMethod]` and workers only ever read the cache.
- **Remesh & Bake: the unwrap wire layout really is sixteen float32 per vertex.** The ABI-3 native struct carried only twelve floats (no reserved tail), so the flat buffer the editor and the native tests read at a 16-float stride was packed at 12 — every field after the first vertex came out shifted garbage (the native test failed on `unit normals` / `normalized UV` on CI, which stopped the plugin rebuild and left the editor on stale ABI-2 binaries). A `static_assert` now pins the struct to the documented stride, and the version-mismatch error states the expected and loaded ABI plus the update/restart steps instead of a bare "Unsupported remesh native ABI".

### Changed
- **Beauty reflections and light response follow the game's specular and bump paths.** Probe reflections previously used one sharp readback lerped toward a flat average with an ad-hoc Fresnel. They now walk the game's specular path: URP's `BoxProjectedCubemapDirection` for box-probe localization, a six-level prefiltered equirectangular mip chain read back per probe (through a `texCUBElod` blit) selected by the pipeline's `r(1.7−0.7r)·maxMip` remap with the fractional part lerped between levels, and `EnvironmentBRDFSpecular` verbatim — surface reduction `1/(r²+1)`, grazing term `saturate(smoothness+reflectivity)`, Schlick Fresnel on NdotV. All Beauty lighting — the directional-lightmap half-Lambert, realtime N·L, ambient and the specular view dot — now evaluates against the source's normal-map-perturbed normal, exactly as the game shades.
- **Beauty bakes reproduce the game's exact directional-lightmap response.** The lightmap colour was sampled flat, which overlights every surface whose normal disagrees with the baked dominant direction — the game shades those surfaces darker. The bake now ports URP's `SampleDirectionalLightmap` verbatim: the direction texture's encoded dominant direction (captured per face alongside the colour map, linear readback) is dotted with the surface's world normal as a half-Lambert and the result divides by the texel's rebalancing coefficient. Non-directional scenes keep the flat colour. For reference, Unity's official HLODSystem was studied for its texture/lightmap combination — it contains none (HLODs render unlightmapped through light probes and realtime light); the authoritative compositing lives in the render pipeline's runtime shaders, which is what this ports.
- **Remesh & Bake: the tangent frame is regenerated after the UV cut, in meshoptimizer.** The unwrap output now carries per-vertex tangents from `meshopt_generateTangents` (MikkT-compatible, with mirrored-UV corner conflicts splitting their vertex), computed over the final atlas layout, so the bake, the 3D preview and the saved FBX all encode against one basis instead of Unity's separate recalculation (which stays only as a fallback). Vertex normals are likewise regenerated in C# from the split geometry for **every** hard-edge mode — crease splits and chart borders are already vertex splits, so the weighted accumulation smooths inside each split group and stays hard across creases and island borders alike. A new **Normal weighting** option (Blender Weighted Normal modifier analog) selects the accumulation weight: *Face area* (meshopt's own, default), *Corner angle*, or *Face area × corner angle*; the stale disablement of the smoothing slider in island modes is gone. Native ABI is now 3 with a sixteen-float unwrap vertex layout (position, normal, UV0, tangent) — the plugins must be rebuilt (**Build Native Libraries**, the auto workflow does it on push) and Unity restarted.
- **Remesh & Bake: Normal smoothing now applies after UV generation and works with every hard-edge mode.** Previously the value was passed into the native normal generation *before* the unwrap, where the UV-island hard-edge rebuild (`SmoothWithinSplitVertices`) overwrote the result — leaving the slider dead in the default mode. The pass now runs in C# on the split unwrap output for all modes (a port of meshoptimizer's alignment-weighted smoothing kernel: up to 10 passes, each averaging normal deltas over mesh edges with aligned neighbours pulling more and opposing ones not at all). Because crease splits and chart borders are already materialized as vertex splits by that point, and mesh edges never cross a split, smoothing flows along the surface and stops at every hard edge regardless of its source. The native call now receives `smoothing: 0`; the ABI and binaries are unchanged.

### Added
- **Remesh & Bake: bounding-box source shape — a far-LOD box proxy.** The remesh stage's new *Shape* selector picks *LOD0* (voxelize the capture, as before) or *Bounding box*: every captured model is replaced by its axis-aligned bounding box, and THAT mesh flows through the rest of the pipeline unchanged — unwrap gives the box its own atlas, and the bake projects the ORIGINAL captured geometry's materials and lighting (Materials and Beauty alike) onto the box's faces. The weld lane produces one box for the whole model; keep-hierarchy produces one box per node. Voxel controls disable in box mode, and the stage cache key covers the shape.
- **Remesh & Bake: Beauty mode — the object baked as the player sees it, into one lit texture.** A new *Bake mode* selector folds the scene's lighting into the BaseColor map and saves an **Unlit/Texture** material with that single texture (the other maps still export alongside for reference): realtime/mixed direct light with hard shadow rays through the source BVH, the renderer's baked lightmaps (captured per face at its UV2 with the renderer's scale/offset, RGBM/HDR decoded at readback), ambient (flat, trilight, or the ambient probe evaluated by Unity itself into a direction grid the bake workers interpolate) and reflection probes (equirectangular readbacks, roughness-lerped toward the probe average, simplified Standard Fresnel). Baked-only lights are skipped so lightmapped faces count nothing twice; the lightmap is the base there (it already contains albedo × GI × baked emission). Specular is view-dependent — it bakes for the scene view camera's position at bake time. Beauty previews render unlit, matching the saved material. Works in both the weld and keep-hierarchy lanes.
- **Remesh & Bake: LOD0-only source filtering.** A *LOD0 only* toggle (default on, remesh stage) skips meshes named `Name_LOD1` and higher wherever they sit — LODGroups already contribute LOD0 only, but machines that emit LOD levels as separate nodes produced duplicate captures.
- **Remesh & Bake accepts skinned sources.** `SkinnedMeshRenderer` geometry is baked at its current pose into a fresh runtime mesh (renderer-local space, no transform scale — the shared root-local capture applies the transform exactly once, like a static mesh under the same node) and captured with its skinned normals/tangents and materials. Pose the model the way you want it baked; animation is not followed.
- **Remesh & Bake: normalized saves and a `remesh` output folder next to the source.** *Normalize size (saved at scale 1)* (default on) bakes the source root's world scale into the saved geometry, so the model keeps its real size with an identity transform regardless of how the source is scaled — positions scale component-wise, normals and tangents take the inverse (inverse-transpose of a diagonal matrix) and renormalize, and a mirroring determinant flips the triangle winding. Off keeps root-local geometry and carries the scale on the saved transform instead (the previous behavior). Saves also no longer ask for a folder when the source resolves to an imported asset: output goes to a `remesh` subfolder next to the source's FBX/prefab/mesh (created if missing), with the folder picker remaining only for scene-only sources.
- **Remesh & Bake diagnostic log (RemeshDiag category)** — every bake prints its health counters to the Console when the `RemeshDiag` log filter is on: welded cage positions and split copies (how many vertices the UV/crease splits duplicated), one-sided border normals with the max cage deviation, nearest-fallback projection samples, missed texels, and the normal map's tilt statistics (mean/max angle from flat, count of texels leaning >45°). A map where >5% of texels lean >45° at a >30° mean tilt additionally logs a warning pointing at hard-edge mode / projection distance / cage fit — the numeric signature of the island-border artifact bands fixed this release. The category bit slots into the existing Log filters panel automatically.
- Experimental Remesh & Bake pipeline with new UV0, material transfer and isolated native jobs.
- **Remesh & Bake: embed textures in the exported FBX** — a save-time toggle (default on) passes `EmbedTextures` to the FBX exporter so the baked maps travel inside the binary FBX instead of being linked by absolute path (the exporter's default link triggered "absolute reference to this texture file" warnings and made the FBX non-portable). Turn it off for a lean FBX that links the exported maps on this machine; embedding adds the map sizes to the file (the float EXR emission map alone is 16 bytes per texel).
- **Remesh & Bake stages and previews** — the tab runs Voxel remesh → Simplify → Normals & UV → Bake as separate stages with their own settings, each re-runnable without repeating earlier ones (stale stages are marked and refreshed first). A right-hand preview shows an orbitable 3D view of any stage (wireframe, shading, baked base color, vertex colors), the UV layout with tinted islands over the baked base color, and every baked map. Simplification is error-driven and adaptive by default (flat areas collapse, detail keeps density) with optional light/strong regularization, fold preservation and small-part pruning; the triangle count is now a stopping point, not required. Hard edges: smooth, crease angle, UV island borders, or both. xatlas island and packing options are exposed. Baking supersamples 1/4/9/16 samples per texel with conservative edge coverage and can transfer source vertex color and vertex alpha to the result mesh. Native remesh ABI is now 2 (`meshLabVoxelRemesh`, `meshLabSimplify`, `meshLabUnwrap`); rebuild the plugins.
- **Remesh & Bake saves the model as FBX** — with `com.unity.formats.fbx` installed, "Save FBX, maps & prefab…" exports the remeshed mesh (generated normals including UV-island hard edges, UV0, tangents, transferred vertex colors) as a binary FBX named after the source, plus a prefab that instantiates it with the curated material; the mesh `.asset` is no longer produced in that mode. The FBX importer of the result is set to `materialImportMode: None` so no duplicate MaterialDescription material is generated. Without the package the previous `.asset` + prefab output remains, with an inline hint to install it. New-geometry FBX writes are documented as the single out-of-core carve-out in `Documentation~/FBX_PIPELINE_CHECKLIST.md` §12.
- **Per-target Transfer diagnostic summary** — `GroupedShellTransfer.TransferCore` emits a single Info line at the end of every transfer when `UvtLog.Category.TransferDiag` is enabled in the Log filters panel. Reports source/target shell counts, `shellsMatched` / `shellsRejected`, `ShellStatus` histogram (Accepted/Degraded/Poor/Rejected/Unmatched), method histogram (interp/xform/merged), `fragmentsMerged`, `dedupConflicts`, `shellsOverlapFixed`, `consistencyCorrected`, mean/max 3D match distance, and topology iterations/fixed/capHit. Lets the identity-sanity and per-LOD ratio sweep checklists in `Documentation~/TRANSFER_LOD_QUALITY_PLAN.md` be run without trawling verbose per-shell output. New category bit slots into the existing Log filters UI automatically.
- **`UvProgress` service** (`Editor/Framework/UvProgress.cs`) — central non-modal progress reporting. Routes status to `UnityEditor.Progress` (Background Tasks panel) plus an inline strip drawn at the bottom of the hub window. Supports nested scopes, phase labels, indeterminate/determinate fractions, cooperative cancellation via `UvProgress.CancelRequested` (`Volatile.Read`-backed `_cancelFlag` so background `Task.Run` work observes user-cancel reliably across the memory barrier), a thread-safe `ReportFromBackground` for `Task.Run` callers (with `Interlocked.Exchange`-guarded snapshot/clear so a racing writer can't lose an update; the `EditorApplication.update` pump is hooked once on assembly load from the main thread via `[InitializeOnLoadMethod]`), and a `Last` outcome shown while idle.
- **Inline progress strip in `UvToolHub`** — sits at the bottom of the window as a status bar. Reserves fixed height unconditionally so toggling active state doesn't displace any layout. Shows title · phase · detail · elapsed in distinct columns with a Cancel button pinned to the right; while idle displays `✓ / ✗ Last-operation · 12.3s`. Marquee animation for indeterminate fractions; orange tint while cancelling.
- **Async pipeline (no main-thread freeze)**:
  - `XatlasRepack.RunPackCancelableAsync` polls the native pack via `EditorApplication.update` + `TaskCompletionSource`. Main thread stays free while xatlas runs in `Task.Run`; the "Hold on — Waiting for Unity's code" dialog no longer fires.
  - `XatlasRepack.RepackMultiAsync` shares its body with sync `RepackMulti` via a `PackFn` delegate.
  - `GroupedShellTransfer.TransferAsync` extracts mesh data on the main thread, then runs the algorithm body in `Task.Run`. Phase reports surface live in the strip via `ReportFromBackground`; `Cancel` mid-transfer is honoured at every phase boundary.
  - `LightmapTransferTool` exposes `ExecRepackAsync` / `ExecRepackPerMeshAsync` / `ExecTransferAllAsync` / `ExecFullPipelineAsync`. Repack, Transfer, and Run Full Pipeline buttons fire-and-forget the async variants; sweep / auto-tune internal loops keep the sync wrappers.
- **Pipeline section in Setup tab** — replaces the freestanding "Repack" header + scattered SymSplit toggles. Numbered stage rows (1 Analyze, 2 Weld, 3 SymSplit, 4 Repack, 5 Transfer) with on/off toggles, coloured state stripes, nested per-stage settings, and right-aligned outcome icons (`…` running / `✓` success / `✗` failed / `⏭` skipped). The big "Run Full Pipeline" button respects every stage toggle; a "Run Repack only" / "Run Transfer only" shortcut pair below it skips the upstream stages for iteration loops.
- **Transfer tab status icons** — `✓` (every entry transferred) / `◐` (partial) / `•` (pending) on each LOD foldout with mesh count and aggregate vertex coverage; per-mesh sub-rows mirror the same icon plus shells-matched and coverage %. Replaces the previous crude single-letter `V` / `O` markers.
- **Project Settings ▸ Mesh Lab ▸ Maintenance ▸ Reset to defaults** — restamps every Mesh Lab project setting back to its built-in default value (Repack defaults, Output path, Sidecar mode, Vertex AO defaults, Show Debug UI). Confirmed via DisplayDialog; existing sidecars / scene state untouched.
- **Explicit Weld stage toggle** — previously Weld always ran inside the full pipeline with no UI to opt out. Now a top-level stage with its own checkbox.
- **Texel-density preview under Repack stage** — `area X m² · density Y tex/m → atlas Z px` (auto) or `→ effective ≈ Y tex/m` (manual). Mirrors the Repack tab preview so users see the resolved atlas size from the Setup tab without switching tabs.
- **Repack tab redesigned** — flat 25-row foldout replaced with five collapsible sections: Resolution, Pack Quality, Density, Compression, Advanced (debug-only). Settings grouped by domain so the panel is navigable.
- **Project Settings ▸ Mesh Lab ▸ Developer ▸ Show Debug UI** — single toggle that hides every diagnostic / benchmarking surface in the package:
  - Setup tab: `Parameter Sweep`, `Log filters`, `UV0 Analysis & Fix`, the SymSplit `Apply to target LODs (advanced)` toggle.
  - Repack tab: the `Advanced (debug)` section (manual texels-per-UV-unit, post-pack density correction, SymSplit threshold mode).
  - Unity top menu: `Mesh Lab ▸ Export FBX Metrics (Selected Assets)` and `(Scene LODGroup)`.
  - Assets ▸ Create context menu: `Mesh Lab ▸ Sweep Test Suite` (was always-visible `Lightmap UV Tool/Test Suite` via `[CreateAssetMenu]`).
  - On-disk transfer-analysis artefacts: `BenchmarkRecorder.NewRun` returns a no-op scope when the toggle is off, so Run Full Pipeline / Repack / Transfer All no longer create `<projectRoot>/BenchmarkReports/` and its per-mesh `*_png/` subfolder on a production run. Existing report folders on disk are left untouched.
- **In-flight gate + `FireAndForget` helper** for fire-and-forget async UI actions. The "Run Full Pipeline" / "Run Repack only" / "Run Transfer only" / "Repack All" / "Transfer All Targets" buttons now sit inside an `EditorGUI.DisabledScope` on `_pipelineInFlight` so a second click can't launch an interleaving run; `FireAndForget` attaches `ContinueWith` on the Unity sync context to log Task faults through `UvtLog.Error`, release the gate, and call `UvProgress.Fail` so a thrown exception can't leave the strip stuck on a stale phase.
- **Cancel-aware benchmark recording** in `ExecTransferAllImpl` — mirrors the `completedSuccessfully` guard `ExecFullPipelineImpl` already uses. Cancelled transfers no longer emit stale `shellTransferResult` / validation rows that taint sweep aggregates.
- **Solid Color bake mode.** Fills `mesh.colors32` with a uniform `Color32` across every mesh variant of an entry (`originalMesh`, `repackedMesh`, `transferredMesh`, `fbxMesh`). Collision meshes are skipped via `MeshHygieneUtility.IsCollisionNodeName`. Each write goes through `Undo.RecordObject` + `EditorUtility.SetDirty`.
- **Batch variant export pipeline.** New `VariantExportPipeline` (`Editor/Tools/VariantExportPipeline.cs`) drives a list of `(Color, suffix)` variants against a source FBX and an optional source prefab. Per variant it paints, exports `{base}_{suffix}.fbx`, instantiates the source prefab, unpacks it (full clone, not a Prefab Variant), swaps `MeshFilter.sharedMesh` to the matching new sub-mesh by name, and saves `{base}_{suffix}.prefab`. Each variant completes its own export → import → clone cycle, with a single `AssetDatabase.Refresh` at the end. Suffixes are validated against `^[A-Za-z0-9_]+$` and duplicates inside a batch are rejected upfront. Conflicts are overwritten — git is the rollback path.
- **`LightmapTransferTool.ExportVertexColorsToFbxAs(sourceFbxPath, outputFbxPath, entries, uvChannelOverride)`** — public entry point that writes a new FBX next to (or anywhere relative to) the source without mutating the source importer, scene mesh bindings, or working copies.

### Changed
- **Remesh & Bake: the 3D preview applies the baked normal map.** A new **Bump** toggle in the 3D view (default on) shades the result stage with the baked tangent-space normal map — the same data the saved material gets — instead of vertex normals only. With *Hard edges = UV islands* the result's vertex normals are smooth by design and all detail lives in the normal map, so the vertex-only preview (and the vivid pink/cyan atlas under **Maps ▸ Normal**) read as "normals not transferred" although the bake is intact. The FBX save also pins `importNormals: Import` and `importTangents: Import` on the result importer, so Unity does not recalculate MikkT tangents whose per-vertex handedness can disagree with the frame the map was baked against (a green-channel flip on parts of the baked map).
- **Remesh & Bake: hard edges default to UV islands and settings persist.** `RemeshSettings.hardEdges` now defaults to `UvIslands`: on coarse organic decimations an angle crease leaves nearly every edge split (a fully faceted look) and feeds xatlas the crease-split normals as seams, which shatters the atlas into sliver charts. Stage settings are also saved to EditorPrefs across domain reloads and tab switches instead of silently resetting to defaults between runs.
- **meshoptimizer v1.3** — `Native~/CMakeLists.txt` now pins the v1.3 release (`9e1f07b1`, 2026-09-25) instead of the 2026-09-10 remesh snapshot (`ff4a519a`). v1.3 drops the no-op `meshopt_RemeshThicken` and renumbers `meshopt_RemeshShell` / `meshopt_RemeshSolve`; the remesh bridge maps its own flag bits by name, so the C# ABI (`meshLabRemeshVersion() == 1`) is unchanged, and the native test now fails if the shell flag stops reaching meshoptimizer. The plugin binaries are rebuilt by **Build Native Libraries**.
- **Vertex AO tab renamed to Vertex Color Baking.** `VertexAOTool` → `VertexColorBakingTool`, `ToolId` `vertex_ao` → `vertex_color_baking`. Asset GUID preserved so existing references stay intact. AO functionality is unchanged and reachable via the new toolbar at the top of the tab. Note: `UvToolHub.SelectToolById` is fed from persisted state, so a stored deep-link to the old `vertex_ao` id now falls back to the first tool.
- **Mesh Lab window menu path** moved from `Tools ▸ Mesh Lab` to `Tools ▸ Mesh Lab ▸ Open Mesh Lab`, making room for `Tools ▸ Mesh Lab ▸ Validators/*`. Existing keyboard shortcuts bound to the old path need rebinding.
- **Log prefix** `[LightmapUV]` → `[MeshLab]`, and the `UvtLog` EditorPrefs keys `LightmapUvTool_LogLevel` / `LightmapUvTool_LogCategoryMask` → `UnityMeshLab_*`. Stored log-filter preferences reset once.
- **Tool shader ids** `Hidden/LightmapUvTool/*` → `Hidden/UnityMeshLab/*`. These are resolved by name at runtime and both ends moved together. `CleanupTool` still recognises the legacy `Hidden_LightmapUvTool` / `Hidden/LightmapUvTool` prefixes so **Fix Materials** keeps cleaning FBX files exported before the rename.
- **Default output paths** moved to `Assets/UnityMeshLab/Output` and `Assets/UnityMeshLab/GeneratedCollisionMeshes`. Serialized settings keep their existing value, so current projects are unaffected; previously generated collision meshes stay on disk but are no longer discovered under the default path.
- **The FBX scripting define remains `LIGHTMAP_UV_TOOL_FBX_EXPORTER`.** PR #118 proposed renaming it to `UNITY_MESH_LAB_FBX_EXPORTER`; that rename was deliberately not carried into this merge, because PR #103 added six further `#if` sites that the rename did not cover — taking it would have silently compiled the whole FBX export path out. Renaming it remains an open decision for a separate, self-contained change.

### Notes for the planned transfer rework
- `BenchmarkSweep.ComputeScore` weights `uv2DuplicatePairs` / `force3DOverlapCount` / `compositeBrokenCount` / `severeMismatchCount` at −50 / −30 / −10 / −20, and `BenchmarkRecorder` reads them off `GroupedShellTransfer.TransferResult`. **A rewritten transfer that does not populate those four fields reports 0 for all of them and therefore scores *higher* than the current pipeline on identical geometry.** Carry the contract into the rework deliberately, or the first A/B comparison will lie.
- `GroupedShellTransfer` skips the quadratic `FindOverlapGroups` scan above `kMaxShellsForOverlapScan = 512`, so `shellsOverlapFixed` under-reports on meshes with more source shells than that and those sweep cells rate better than they are. This is a reading caveat, not a bug to "fix" by removing the guard.
- **Default `RepackResolutionMode` is now `AutoFromTexelDensity`** (was `Manual`). Uniform real-world texels-per-meter is the desired outcome for lightmaps; the previous fixed-resolution default produced wildly different texel density per asset depending on world size.
- **xatlas pack no longer raises a modal progress dialog** (`EditorUtility.DisplayCancelableProgressBar`). Progress now flows through `UvProgress` to the Background Tasks panel and the inline strip; cancel via `UvProgress.CancelRequested`.
- **Hoisted all late `sourceMesh.*` and `targetMesh.*` reads** in `GroupedShellTransfer.Transfer` up to the initial Mesh-data extraction block. Keeps the algorithm body Unity-API-free so it runs cleanly in `Task.Run`.

### Refactored
- `LightmapTransferTool.ExportVertexColorsToFbxCore` now accepts an optional `outputFbxPathOverride` and returns `bool` for success. When the override is set and differs from the source path, Phase 1 (source importer mutation), Phase 4 scene relink, and Phase 5 restore are skipped so the source FBX and live scene stay untouched. Existing overwrite and hierarchy-mode callers keep their void-style usage.

### Removed
- All `EditorUtility.DisplayProgressBar` / `DisplayCancelableProgressBar` / `ClearProgressBar` calls. Replaced with `UvProgress` everywhere (XatlasRepack, LightmapTransferTool, VertexAOBaker.Cpu, FbxMetricsExporter, LodGenerationTool).
- `[CreateAssetMenu(menuName = "Lightmap UV Tool/Test Suite")]` from `TestSuiteAsset` — replaced with a gated `[MenuItem("Assets/Create/Mesh Lab/Sweep Test Suite")]`.
- Transfer tab decluttered: removed the misplaced `Generate LODs` section (full LOD-Gen UI lives in the dedicated LOD Gen tab), the `FBX Export` block (Export as New / Overwrite Source — both duplicates of the sidebar footer's `Export New FBX` / `Overwrite FBX`), and the `Save FBX from main (_main)` button (duplicate of the footer's `Backup from main`). Transfer tab now reads: per-LOD status list → `Transfer All Targets` → `Quality Report` → `Validation Overlay` → `Apply UV2` actions.

### Security
Consolidated hardening sweep — incorporates PRs #122–#123, #125–#129, #131, #134–#140, #142–#145, #147–#149, #151–#152, #156–#159, #161–#164, #168 and #170–#190.

- **Process invocation and gallery paths (Mimosa pre-commit gate)** — `UvToolHub.StartGitBinary`/`RunGit` and `BenchmarkSweep.TryRunGit` no longer assemble git command lines as strings: every argument reaches git as one verbatim `ProcessStartInfo.ArgumentList` token, so an asset path interpolated into a revision like `main:<path>` cannot inject extra git options. `Tools~/build_gallery.py` routes every write through `write_gallery_file`, which resolves the output directory, requires the target to resolve to a direct child with the exact sanitized basename (`safe_model_filename` strips everything outside `[A-Za-z0-9._-]` from CSV-derived `lodGroup` names), and refuses anything that would escape the gallery directory.
- **CI and build tooling** — release/version workflows no longer interpolate untrusted values straight into inline Python; the native build workflow pins every action to a verified SHA and defaults to a read-only token, with `contents: write` scoped to the publish job alone, which still commits the freshly built binaries into `Plugins/` automatically on push; `gen.bat` and the skills-overhaul parameter file quote/escape their arguments; `.npmignore` mirrors `.gitignore` so ignored artifacts can't leak into a published tarball (#123, #125, #135, #136, #164).
- **Vendored xatlas** — `Native~/CMakeLists.txt` builds a pinned, vendored xatlas source tree instead of `FetchContent`-ing a moving upstream branch (#127).
- **Sidecar replay validation** — `Uv2DataAsset` payloads are now treated as untrusted input: UV channel dimensions and lengths, submesh triangle counts, collision entries, vertex-color arrays, xatlas settings and the sidecar-supplied save path are validated before use, stale/incomplete remap rebuilds abort instead of half-applying, and remap work is bounded (#126, #139, #151, #182, #185, #188, #189).
- **Resource limits** — bounded or overflow-checked work budgets across Vertex/GPU AO (bake workload, seam matching, 3D and 2D blur, face-area correction, NaN-safe atomic accumulation, deferred GPU cleanup), repack (atlas oversample sizing, pack cost, exclusive native xatlas sessions, cached area preview), grouped transfer and spatial overlap scans, SceneView hover raycasts, benchmark sweep parameters and UV PNG generation, ARAP UV ranges, FBX backup path hashing, V-HACD recursion depth and triangle indices, and the meshoptimizer packing buffers feeding the unsafe/native path (#122, #128–#129, #131, #134, #137–#138, #140, #143–#144, #147–#149, #152, #162–#163, #184, #186–#187, #190).
- **Output escaping** — a shared `CsvUtil.Escape` neutralises formula prefixes in every CSV writer (sweep summaries, FBX metrics, benchmark records) and the gallery generator escapes model names before embedding them in HTML hrefs (#157–#159, #161).

### Fixed
- **Remesh & Bake: zero normals from the unwrap now self-heal and are reported.** A degenerate corner group (zero-area faces) can come back from the native unwrap with zero-length normals, which zero the bake's ray directions and tangent frames at once — the diagnostic signature is every cage deviation reading 90°, ~100% of projection samples falling back to nearest-point search, and a normal map whose blue channel sits at 128 (tilt ≈ 90°) everywhere. The UV stage now detects zero normals, rebuilds all of them from face geometry (`SmoothWithinSplitVertices`) with a warning, and the RemeshDiag summary reports the zero count plus a dedicated warning when more than 80% of samples fall back to nearest-point search.
- **Remesh & Bake: saved model now matches the source's world size.** The result geometry is captured and baked in the source root's local space (capture combines renderers via `root.worldToLocalMatrix`, with inverse-transpose normals and winding sign — source and target share one space, so projection scale was always consistent), but the export temporary carried an identity transform, so a source with `lossyScale != 1` (FBX file scale, artist-scaled GameObject) saved at root-local size — a different size than the original. The exported FBX node (and the `.asset` fallback prefab) now carries the source root's lossyScale. The RemeshDiag summary additionally reports the source/target bounds-diagonal ratio and warns when it drifts from 1 beyond 10%, catching future scale regressions at bake time.
- **Remesh & Bake: normal-map artifact bands around every UV island in hard-edge modes.** With *Hard edges = UV islands* (or *+ angle*), `SmoothWithinSplitVertices` leaves each chart-border vertex copy a one-sided normal, and the baker cast its projection rays along exactly those normals — the ray landed on a displaced source point, so a band of garbage texels formed along every island border (on an 85-island atlas: noise everywhere). Rays are now cast along a smooth welded cage direction (area-weighted face normals averaged across bit-identical coincident vertices — the direction the native crease=π path produces before the split), while the tangent frame keeps the hard vertex normal the result mesh shades with, so encode/decode still cancel and hard island borders survive intact. Verified on a synthetic displaced-sphere repro (exact port of the bake math): mean angular error drops from 3.2° to 0.96° and >45° outliers fall 8× to the level of the previously-working smooth-border path.
- **"Save Mesh Assets" no longer throws on unparsable mesh names** — `SaveAll` sanitized mesh names before generating asset paths; a name with a character that is invalid in file names made `GenerateUniqueAssetPath` return an empty string and `CreateAsset` threw "path is empty" mid-OnGUI, corrupting the hub layout state for the event. A missing/uncreatable output folder now aborts with a readable error instead of cascading.
- **FBX re-saves no longer triangulate quad meshes** — the isolated-export core (`ExportFbxIsolatedCore`, the path behind Vertex AO saves and narrow-intent UV/channel overwrites) enables `keepQuads` on the source importer in Phase 1, so the clone it serializes keeps the FBX's original polygon topology and Unity's FBX exporter writes quads back. `keepQuads` only reshapes the index buffer (vertex order/count untouched), so the snapshot/clone vertex-count contract holds, and the setting persists like `generateSecondaryUV=off` so the re-saved file is not re-triangulated on the next import. Variant exports (`ExportVertexColorsToFbxAs`) toggle `keepQuads` only for the clone reimport, restore the source importer afterwards (also on failure), and pin `keepQuads` on the new variant file's own importer. The wide LOD-rebuild path already locked `keepQuads` at export time; its export meshes still take topology from the tool's computed meshes, so the first wide export of a freshly loaded (triangulated) import writes triangles — from the next reload on, quads survive. N-gons (>4 vertices) still import triangulated (`keepQuads` covers quads only).
- **Remesh & Bake capture and save** — Read/Write-disabled source meshes are read from the imported asset instead of an empty `Instantiate` clone ("Not allowed to access uv"), shaders other than Standard/URP Lit bake from common property names with a warning instead of aborting, and Save refreshes a folder the panel just created instead of failing with "Could not create result folder".
- **Remesh & Bake tab** — Source root now follows the selection (a LOD child resolves to its LODGroup) instead of being read once when the tab first opened, which left Generate & Bake disabled after selecting a model as the status text asks. Generate & Bake and Save end the IMGUI event after they change the sidebar or open the modal folder panel, so the first run no longer logs a `Getting control N's position` layout error. Cancel reports that it is waiting for the current phase, and the result line reads the index count instead of copying `Mesh.triangles` on every GUI event.
- **`MeshoptNative` simplify flags** — `SimplifySparse` / `SimplifyErrorAbsolute` were swapped relative to `meshoptimizer.h` (`Sparse = 2`, `ErrorAbsolute = 4`). No caller passed either flag, so behaviour is unchanged.
- **UV transfer / repack correctness** — Repack normalizes UV0 shell winding at its own API boundary instead of assuming the optional Weld stage ran; source meshes with incomplete (non-empty but short) UV0/UV2 channels are rejected before indexing; cancelled transfers drop their partial UV2 output; post-pack density correction stays inside each packed chart; `RepackUv` forwards its rotate flag to xatlas; the UV2 clamp toggle is independently editable; `Apply UV2` is reachable after a source-only repack; cross-LOD hints are isolated per mesh group (#128, #156, #170–#173, #180–#181, #183).
- **Undo and preview handling** — undo/redo restores preview-swapped meshes before rebuilding `MeshEntries` so a preview mesh can't become the cached baseline; Vertex AO no longer takes duplicate preview backups and clears stale results before loading new mesh data; the shell colour hash survives `int.MinValue`; Inverted triangles are rendered in the validation overlay instead of drawing clean or vanishing under the filter (#145, #168, #174, #176–#177).
- **LOD parsing and generation** — LOD indices parsed from object names are bounded, and LOD0's transition height is normalized before generating the remaining levels (#142, #179).
- **FBX export** — `NormalizeExportHierarchy` bakes each node's transform into a per-node mesh copy, so a shared FBX sub-asset is no longer mutated cumulatively (#175).
- **Collision meshes** — `StripCollisionMesh` calls `mesh.Clear(false)`; with the default `keepVertexLayout=true` the extra tangent/color/UV channels were retained and the strip was a no-op (#178).

## [1.0.5] - 2026-05-13

### Added
- **Pre-pack shell snap to integer atlas pixels** (`SnapShellsToIntegerPixels`, default ON). Before handing shells to xatlas, each shell is scaled per-axis around its UV centroid so its bbox extent is an integer number of atlas texels. Makes xatlas's own per-chart `ceil(extents)` rescale (xatlas.cpp ~line 8345, upstream Issue #18 wontfix) a no-op for every chart, so the uniform per-shell density set up by `TexelDensityNormalizer` survives the pack instead of being amplified by sub-pixel rounding. **No xatlas fork required** — the package builds stock xatlas master via `FetchContent`.
- Auto-resolution mode for Repack (`ResolutionMode.AutoFromTexelDensity`) — pick a target texels-per-meter density and the tool computes the atlas resolution from total 3D area. Manual mode unchanged. Sweep automatically forces Manual so each cell's `atlasResolutions[i]` is the resolution xatlas actually packs at.
- `[Density]` / `[Density:snap]` / `[Density:postUV2]` diagnostic logs at multiple pipeline checkpoints (snap / postAssign / postOrphan / postBorder / postCorrection / final) so per-shell density drift is visible per stage.
- Cancellable xatlas pack — `xatlasPackCharts` now runs on a background `Task` while the main thread polls `DisplayCancelableProgressBar`. xatlas itself has no native cancel API, so cancel = wait for in-flight pack to finish, then discard result and stop pipeline.
- Pack cost-budget preflight — refuses packs that would take many minutes (brute-force budget 500M ops, heuristic 20B). Auto-disables brute force above the budget instead of hanging the editor.
- UI toggles in Pre-pack panel: `Snap shells to integer atlas pixels (pre-pack)`, `Post-pack density correction (experimental)`, `Internal pack oversample` popup.

### Fixed
- Per-shell lightmap texel density variance on real artist UVs (target ~1× from ~14× spread). Achieved purely via input preparation — no xatlas patch required.
- Sweep `atlasResolutions` dimension was collapsed to a single value when `RepackResolutionMode == AutoFromTexelDensity` (every cell recomputed and overrode the swept value). Sweep now snapshots and forces Manual for the duration.
- `BenchmarkRecorder` `atlasRes` column now reflects the resolution xatlas actually packed at (post auto-compute), not the raw `ctx.AtlasResolution` UI setting.
- Benchmark per-mesh records now skip entries with `include == false` so user-deselected meshes carrying stale `TransferResult` / `ValidationReport` don't surface as failed rows in sweep aggregates.
- `BenchmarkSweep` `totalMs` no longer double-counts inner stages — `pipelineMs` is the outermost wall clock and already contains repack + transfer + validate; summing all four triple-counted inner work. Standalone Repack/Transfer rows (no pipeline wrapper) fall back to the sum of inner stages.

### Changed
- `TexelDensityNormalizer` now logs a rich `[Density] pre … post … scale …` summary at Info level instead of a terse "rescaled N/N shells" line.
- `Native~/third_party/xatlas/` removed; `Native~/CMakeLists.txt` reverted to `FetchContent_Declare(xatlas)` against upstream master.

## [1.0.0] - 2026-04-20

### Changed (breaking)
- **Package identifier** renamed from `com.sasharx.lightmap-uv-tool` to `com.sasharx.unitymeshlab` to align with the repository name and the canonical `com.sasharx.<lowercase-package>` rule in `.claude/skills/_shared/naming-conventions.md`. Downstream consumers must update the entry in `Packages/manifest.json`.
- **Root namespace** migrated from `LightmapUvTool` to `SashaRX.UnityMeshLab` across all 48 Editor C# files. The asmdef `name` and `rootNamespace` were updated and the asmdef file was renamed `LightmapUvTool.Editor.asmdef` → `SashaRX.UnityMeshLab.Editor.asmdef` (GUID preserved). External code that referenced the bare `LightmapUvTool` namespace must switch to `SashaRX.UnityMeshLab`.
- **`repository.url`** updated from `UnityLodUvLightmapTransfer.git` to `UnityMeshLab.git` to match the current canonical GitHub URL.

### Added
- `documentationUrl`, `changelogUrl`, and `licensesUrl` fields in `package.json`.
- `.claude/skills/` overhaul — English-language skill catalog with progressive disclosure, `_shared/` references, `_checklists/`, `_template/package-template/`, and three new skills (`unity-ci-validation`, `unity-package-bootstrap`, `repo-conventions`).
- `.prettierignore` protecting skill YAML frontmatter from Prettier reflow.

### Migration guide
- In consumer projects, replace the old identifier in `Packages/manifest.json`:
  ```json
  "com.sasharx.unitymeshlab": "https://github.com/SashaRX/UnityMeshLab.git"
  ```
- In any code that referenced the `LightmapUvTool` namespace, switch to `SashaRX.UnityMeshLab`.

## [0.15.36] - 2026-04-07

### Added — Cleanup tool (#59)
- **New "Cleanup" tab** (ToolOrder 45, between Collision and Vertex AO) for mesh/material/collider/scene hygiene
- **Fix Materials**: detect Hidden_LightmapUvTool shaders on renderers, match LOD1+ materials to LOD0, strip unused material slots
- **Clean Colliders**: strip UV/normal/tangent/color from _COL meshes, detect and remove duplicate _COL objects
- **Scene Cleanup**: remove orphaned LOD objects after FBX reimport, rebuild LODGroup from hierarchy naming
- **Mesh**: batch weld via meshopt, strip empty UV channels, per-LOD vertex/tri count summary
- All operations use Undo and log via UvtLog

## [0.15.9] - 2026-04-06

### Added — Auto LODGroup creation + Vertex AO + CI workflows

#### Auto LODGroup Creation
- **"Add LOD Group" button in Setup tab**: when no LODGroup is assigned, detects LOD siblings by name pattern (`_LOD0`, `_LOD1`) and shows one-click creation button with detected LODs list
- **Fallback for non-LOD naming**: when children have arbitrary names but contain Renderers, creates LODGroup with all renderers as LOD0 and renames children to `_LOD0`
- **Auto-clear stale LODGroup**: `OnSelectionChange` now clears the LODGroup context when selecting a mesh-relevant object without a LODGroup (preserves context when clicking lights/cameras)
- **Shared helpers**: `FindLodSiblings` and `CreateLodGroupStatic` extracted as `internal static` for reuse across tools

#### FBX Export Improvements
- **LOD0 naming in FBX**: exported FBX clone renames children to `_LOD0` matching scene hierarchy
- **Post-export cleanup**: removes scene-generated LOD1+/COL objects after successful export to prevent duplicates on reimport
- **Cleanup gated on success**: only runs when all FBX groups exported successfully
- **Collision dedup**: removes existing `_COL` nodes from FBX clone before adding new ones (prevents accumulation on repeated exports)

#### Vertex AO Baking
- **GPU vertex AO baker**: hemisphere depth sampling via compute shader
- **CPU fallback**: for platforms without compute shader support
- **Depth shader**: `Hidden/LightmapUvTool/VertexAODepth` for orthographic depth rendering
- **Compute shader**: `VertexAOAccum.compute` with AccumulateAO and FinalizeAO kernels

#### Safety & Lifecycle
- **RestoreWorkingMeshes()**: extracted helper from OnDisable, called before clearing/switching LODGroup context to prevent orphaned temporary meshes
- **ForceLOD(-1) reset**: clears forced LOD preview state before dropping LODGroup reference
- **meshoptimizer v1.1**: updated from v0.22

#### CI/CD
- **Meta file validation** (`meta-check.yml`): PR check for missing/orphaned `.meta` files and `package.json` semver
- **Auto version bump** (`version-bump.yml`): auto-increments patch version on push to main
- **CLAUDE.md**: project rules and 10-point review checklist for contributors

## [0.13.32] - 2026-03-07

### Fixed — Unfilled vertices after remap rebuild (3D stretching to origin)
- **Root cause A — Order-dependent fingerprint hash**: `MeshFingerprint.Compute` used sequential FNV-1a over positions/UV0, which depends on vertex order. Unity's FBX reimport can reorder vertices without changing geometry, causing ALL meshes to appear stale on every reimport. Changed to order-independent hash (XOR of per-vertex FNV hashes). Existing sidecars will show stale once (transition), then stable after re-Apply.
- **Root cause B — No used-tracking in `RebuildRemapFromPositions`**: When multiple raw vertices share the same position (UV seams, hard edges), all could match to the same stored vertex, leaving other optimized indices unfilled (zero position/normal/UV0 → 3D stretching). Added `usedStored[]` tracking with priority: unused+valid-remap > unused+invalid > used. Fixes 864/856/323 unfilled vertices on TrainСarriage LOD0/1/2.
- **Normal-based disambiguation**: Added `vertNormals` to sidecar (raw FBX normals). When UV0 alone can't distinguish candidates at the same position (hard edges: same pos+UV0, different normal), normal dot product breaks ties.
- **Fixed matched counter**: No longer double-counts vertices where `origRemap[j] == -1` (Pass 1 increments, Pass 2 re-processes since `newRemap[i] < 0`).
- **Single-candidate reuse**: When only one stored vertex exists at a position, always use it (reuse is safe — same position = same origRemap). Previously returned -1 if already used, leaving 2 vertices unfilled on LOD0/LOD1.
- **Pass 2 allows reuse**: Nearest-neighbor fallback no longer filters by `usedStored` — these are rare bucket-boundary cases where the nearest stored vertex should have the correct origRemap.
- **Fingerprint carry-forward fix**: Detect raw vs postprocessed mesh by comparing vertex count to stored remap length. Compute fresh fingerprint when mesh is raw (after Reset), carry forward only when postprocessed. Ensures new order-independent hash takes effect without requiring manual Reset.

## [0.13.31] - 2026-03-07

### Fixed — Per-face composite UV2 for overlap groups spanning multiple sources
- On lower LODs, one target shell can span multiple source shells (e.g., belt: LOD0 has 13 source shells, LOD2 has 4 target shells covering the same UV0 area).
- Instead of picking one source for all vertices, each target face now gets UV2 from the source that is geometrically closest (per-face 3D proximity vote).
- Shared vertices at source boundaries are handled by last-writer-wins — lightmap padding covers micro-seams.
- Log shows `composite=Nsrc` when multiple sources contribute UV2 to a single target shell.
- All contributing sources are claimed to prevent reuse by other targets.

## [0.13.30] - 2026-03-07

### Fixed — Source claiming prevents same-source UV2 overlap in overlap groups
- **Source claiming**: track which source shells are already used by earlier targets (both interp and merged). Overlap unified ranking prefers unclaimed sources to prevent multiple targets from mapping to the same source, which produces UV2 overlap artifacts.
- **Ranking chain**: `issues → unclaimed → hint → vote winner → vote count → centroid distance`
- Log shows `CLAIMED` when the best available source was already taken by another target.
- Examples: LOD2 t81/t88 both picking src102, or t78/t99/t100 all picking src105 — now the second target is redirected to its next-best unclaimed source.

## [0.13.29] - 2026-03-07

### Fixed — Demote centroid distance to tiebreaker in overlap ranking
- Centroid distance was overriding face-proximity voting (e.g. LOD1 t106: 14/14 votes for src96 but centroid picked src104). A small nearby source shell can have a closer centroid while its individual faces are wrong.
- **New ranking chain**: `issues → hint → vote winner → vote count → centroid distance`
- Face-proximity voting is the most reliable geometric signal (compares individual faces). Cross-LOD hint provides consistency. Centroid distance and area ratio kept for diagnostics and final tiebreaker only.

## [0.13.28] - 2026-03-07

### Added — 3D centroid distance + area ratio diagnostic data
- Precompute per-source-shell 3D area and centroid distance for overlap groups.
- Log `dist3D` and `areaR` in overlap unified output for debugging.

### Fixed (0.13.27) — Deterministic overlap shell matching
- **Vote tie-breaking**: use 3D centroid distance as deterministic tiebreaker when vote counts are identical.
- **Deterministic iteration order**: sort Dictionary keys in dedup phase and overlap group members. Sort shell extraction by root face index for stable shell IDs.

## [0.13.11] - 2026-03-03

### Fixed — UV Preview uses the same checker as model with requested alpha
- In `UvTransferWindow.cs`, UV Preview background now uses the same checker texture source that is applied to the model and renders it with `alpha = 0.33333` when checker mode is enabled.
- Kept fallback checker path, but aligned checker-enabled fallback alpha to `0.33333` as well.
- Version format kept as `xx.xx.xx` (`0.13.11`).

## [0.13.10] - 2026-03-03

### Fixed — UV Preview background parity with model material/checker
- **UV Preview background source** (`UvTransferWindow.cs`): canvas now resolves a real background texture for preview instead of relying only on generic gray checker underlay.
  - If **Checker** mode is enabled, UV Preview uses the same procedural checker texture as Scene preview.
  - Otherwise it takes the first available `_MainTex` from preview LOD renderer materials.
- **Textured UV background draw pass** (`UvTransferWindow.cs`): added textured GL quad rendering for the 0-1 tile and neighboring UDIM tiles so the UV backdrop visually matches model shading reference.
- **Checker texture accessor** (`CheckerTexturePreview.cs`): added `GetCheckerTexture()` to share one checker source between Scene and UV Preview.
- **Editor resource lifecycle** (`UvTransferWindow.cs`): added creation/disposal of the texture drawing material (`texMat`) in `OnEnable` / `OnDisable`.

## [0.13.7] - 2026-03-03

### Fixed — UV0-interp fallback for broken force3D on thin wrapping geometry
- **Pass 2 UV0-only constrained fallback** (`GroupedShellTransfer.cs`): when both
  force3D passes (all-source and constrained) produce high issues (>50% of face
  count), a third pass runs UV0 interpolation constrained to the assigned source
  shell without the force3D override. On thin wrapping geometry (belts, straps,
  bands that wrap around a box), 3D projection maps vertices to scattered edge
  faces of the source, producing 100% face issues and garbage UV2 topology. UV0
  interpolation finds the correct source triangle in UV0 space and produces UV2
  matching the source LOD0 layout.
- **No overlap guard on Pass 2**: same-source overlap from UV0 interpolation is
  geometrically correct for tiling/symmetric geometry — both shells sample the
  same lightmap texels. The overlap guard is skipped to avoid rejecting valid
  candidates.
- **Conservative threshold**: >50% face issues triggers the fallback. Normal
  force3D shells get 0–2 issues; broken belt shells get 100%. Only
  catastrophically broken projections trigger the fallback.
- **Log label**: `UV0-interp fallback` distinguishes from `3D-primary` and
  `src-constrained` in diagnostics.

## [0.13.0] - 2026-03-03

### Changed — SymmetrySplit source-only default + sidecar metadata + validation improvement
- **SymmetrySplit restricted to source LOD by default** (`UvTransferWindow.cs`):
  `ExecSymmetrySplit` now accepts `bool includeTargets` parameter. By default
  only source LOD is processed, preventing accidental topology changes on target
  LODs that could degrade Transfer/Apply stability. UI toggle "SymSplit target
  LODs (advanced / risky)" enables target processing with a warning.
- **Symmetry split flag in sidecar** (`Uv2DataAsset.cs`, `Uv2AssetPostprocessor.cs`):
  new `stepSymmetrySplit` field in `MeshUv2Entry` (schema version bumped to 2).
  Sidecar now records whether symmetry split was applied, enabling deterministic
  replay on reimport. Backward-compatible: `ResolveSymmetrySplitStep()` infers
  the flag for old schema v0/v1 sidecars.
- **`wasSymmetrySplit` per-mesh tracking** (`UvTransferWindow.cs`): each
  `MeshEntry` tracks whether symmetry split modified it. Used in Apply to
  populate sidecar flags and build replay data.
- **Majority vote shell determination** (`TransferValidator.cs`): triangle
  shell assignment now uses 3-vertex majority vote instead of first-vertex
  lookup. If 2 of 3 vertices share a shell, that shell wins; otherwise falls
  back to first valid vertex. Improves stretch outlier detection accuracy for
  boundary triangles.
- **Transfer diagnostics**: source layout logging, vertex count mismatch
  warnings for previously transferred meshes, target LOD modification warnings
  when SymSplit was applied to targets.

## [0.11.3] - 2026-03-03

### Fixed — Cross-source UV2 overlaps from merged shells
- **Constrained search for force3D merged shells** (`GroupedShellTransfer.cs`):
  the 3D-primary merged mode (introduced in 0.11.1 for dedup conflicts) used
  all-source search, which could produce UV2 coordinates in other source
  shells' UV2 regions — overlapping with target shells assigned to those
  sources (e.g. `uv2sh[91](interp,src102) vs uv2sh[93](merged,src104):
  diff-src`). Now constrains force3D search to the assigned source shell's
  faces only, keeping UV2 within the source's UV2 bounds while still using
  3D-primary decision logic for vertex projection.
- **All-source overlap guard** (`GroupedShellTransfer.cs`): for non-force3D
  merged shells that fall back to all-source search, the candidate UV2 AABB
  is checked against claimed source shells' UV2 AABBs. If overlap is
  detected, the constrained result is preferred even with more triangle
  issues — trading minor quality loss for overlap-free lightmaps.
- Eliminates merged-involved diff-src overlaps reported by the validator.

## [0.11.2] - 2026-03-03

### Fixed — Cross-source UV2 overlaps from xform extrapolation
- **Xform bounds check** (`GroupedShellTransfer.cs`): the similarity transform
  (xform) method could extrapolate UV2 coordinates beyond the source shell's
  UV2 region, causing overlaps with shells from different sources (e.g.
  `uv2sh[11](xform,src11) vs uv2sh[109](interp,src117): diff-src`).
- Now precomputes UV2 bounding boxes for each source shell and applies a
  two-level penalty during xform vs interp method selection:
  - If xform output AABB crosses into another source shell's UV2 AABB,
    xform is invalidated entirely — forces interp (which stays within
    source UV2 convex hull by construction).
  - If OOB but no cross-shell overlap, applies mild penalty (OOB vertex count).
- Eliminates diff-src xform-involved overlaps reported by the validator.

## [0.11.1] - 2026-03-03

### Fixed — Same-source UV2 overlaps via 3D-primary merged mode
- **Dedup conflict resolution** (`GroupedShellTransfer.cs`): when multiple
  target shells claim the same source and no unique alternative exists
  (all would force merged mode), previous logic reverted to the overlapping
  source — causing identical UV2 positions and lightmap seams. Now forces
  conflicting shells into a new **"3D-primary" merged mode** that:
  - Skips the constrained pass (which would reproduce the overlap)
  - Uses all-source search with 3D projection as the primary method
  - 3D projection naturally maps each target to its spatially nearest
    source geometry, giving unique UV2 regions without overlap
- Eliminates same-source xform-involved overlaps reported by the validator
  (e.g. `uv2sh[93](xform,src104) vs uv2sh[99](xform,src104): SAME-SRC`).
- New log labels: `merged+3D` during dedup, `3D-primary` in per-shell
  diagnostics.

## [0.11.0] - 2026-03-03

### Added — Multi-criteria source shell rescoring for merged shells
- **RescoreMergedShells** (`GroupedShellTransfer.cs`): new Phase 2a+ step
  between initial shell matching and deduplication. When `DetectMergedShell`
  marks a target shell as merged (because 3D centroid-based matching picked
  a wrong source), the new step evaluates all source shells using a 4-criteria
  weighted score:
  - UV0 coverage fraction (35%) — direct measure of UV0 compatibility
  - Normal agreement (30%) — disambiguates front/back on thin geometry
  - UV0 area ratio (20%) — filters mismatched shell sizes
  - 3D centroid distance (15%) — spatial proximity prior
- If the best-scoring source has UV0 coverage >= 70%, the shell is un-merged
  and reassigned to the correct source, avoiding the lossy all-source fallback.
- **ComputeUv0CoverageFraction** helper: refactored continuous version of
  `DetectMergedShell` logic, returns 0..1 fraction instead of binary bool.
- Precomputed per-shell average face normals and total UV0 areas for both
  source and target shells (used by the scoring function).

## [0.9.94] - 2026-03-03

### Added — Edge analysis + spatial hash optimization (inspired by UnityMeshSimplifier)
- **EdgeAnalyzer** (`EdgeAnalyzer.cs`): new utility for edge-level mesh topology
  analysis. Builds edge-face adjacency via position-group spatial hashing,
  classifies each geometric edge as Border, Interior, UvSeam, UvFoldover,
  HardEdge, or NonManifold. Provides `FindHardEdgeVertices()` for the planned
  hard-edge shell splitting feature, and `FindBorderVertices()` for mesh
  boundary detection. Uses Union-Find on position groups similar to
  UnityMeshSimplifier's Smart Linking approach.
- **SourceGuidedWeld spatial hash** (`Uv0Analyzer.cs`): replaced O(n×m) brute
  force nearest-source-vertex lookup with 3D spatial hash grid (27-cell
  neighborhood search). Cell size auto-computed from source mesh AABB diagonal
  divided by cube root of vertex count. Falls back to brute force per-vertex
  on grid miss. Typical speedup ~10-50× for large meshes.
- **Seam/foldover weld distinction** (`Uv0Analyzer.cs`): SourceGuidedWeld now
  classifies vertex pairs as foldover (same UV0 → weld unconditionally) or
  seam (different UV0 → require same source shell). Foldover pairs no longer
  need the expensive source shell lookup. Logs `foldover:N seam:N` counts.

## [0.9.93] - 2026-03-02

### Added — Consistency check + texel density metric (inspired by CurioMesh)
- **Merged shell consistency check** in `GroupedShellTransfer`: for merged
  shells (multi-source), UV0 projection remains primary but now runs a
  secondary 3D surface projection with backface rejection (dot > 0.3).
  If UV0 hit is distant (sqr > 0.05) and UV2 results disagree (delta > 0.02),
  prefers the 3D result. Logs `consistency-corrected` vertex count.
  Closes the gap where merged shell UV0-only search could silently pick
  wrong triangles from distant UV0 regions.
- **Texel density metric** in `TransferValidator`: computes per-triangle
  `areaWorld / areaUV2` ratio, finds global median, flags triangles where
  ratio deviates > 200× from median as `TexelDensity` issue. New fields:
  `texelDensityRatios[]`, `texelDensityMedian`, `texelDensityBadCount`.
  Visible in Review tab as cyan "Txl" bar and fill color.
- **Backface threshold** (0.3) used in merged shell 3D fallback path,
  filtering source triangles whose normal opposes target vertex normal.
  Previously only used in legacy transfer pipeline.

## [0.9.74] - 2026-03-02

### Fixed — Interpolation primary, transform fallback only
- **Reverted similarity transform from primary to fallback.** Transform
  extrapolates when target UV0 differs from source UV0 (always on LOD meshes),
  causing inter-shell UV2 overlaps. Interpolation is bounded by source UV2
  triangle convex hull and cannot extrapolate.
- Transform now wins only when it has strictly fewer inverted/zero-area
  triangles than interpolation (was: wins on equal).
- Merged shells reverted to per-vertex UV0 interpolation across all source
  triangles (was: per-vertex transform via source shell assignment).

## [0.9.73] - 2026-03-02

### Changed — Similarity transform per-shell (primary transfer method)
- Added ComputeSimilarityTransform: least-squares UV0→UV2 fit per source shell.
- Per-shell transform precomputation with mirrored shell detection.
- Diagnostic logging: xform/interp/merged counts per mesh.
- New TransferResult fields: shellsTransform, shellsInterpolation, shellsMerged.

## [0.7.6] - 2026-03-01

### Added — Reset Working Copies button
- **Reset All Working Copies** button in Setup tab: visible when any meshes
  are modified (welded, repacked, or transferred). Restores all `originalMesh`
  back to FBX originals, clears all derived meshes and caches. Useful when
  re-selecting a previously processed LODGroup.
- **[W] badge** on welded meshes in the mesh list.

## [0.8.3] - 2026-03-01

### Changed — UV0-first transfer with 3D guard (UV Weld Selected philosophy)
- **UV0 is primary search space, 3D is only disambiguation guard.**
  For each target vertex: find nearest source triangle in UV0 2D space.
  If only one candidate → use directly. If multiple at same UV0 distance
  (overlapping UV shells from tiling textures) → use 3D position + normal
  to pick correct triangle. Compute UV0 barycentric → interpolate UV2.
- No shell assignment step needed. UV0 lookup naturally finds correct
  triangle; 3D only resolves ambiguity from overlapping shells.
- Logs "3D-disambiguated" count for vertices that needed overlap resolution.

## [0.8.2] - 2026-03-01

### Changed — Two-phase transfer: shell assignment + UV0-space projection
- **Phase 1: Shell assignment** — for each target vertex, find nearest source
  vertex by 3D distance with normal filter (dot > 0.3). This determines which
  source UV0 shell the target vertex belongs to. Normal filter separates
  front/back of thin walls.
- **Phase 2: UV0-space transfer** — within the assigned shell, find nearest
  source TRIANGLE in UV0 SPACE (not 3D!), compute 2D barycentric coordinates
  on the UV0 triangle, interpolate UV2. This is equivalent to 3ds Max
  "UV Weld Selected" — the actual UV transfer happens in UV space, eliminating
  all thin-wall / overlapping geometry issues.
- Shell identity comes from 3D geometry; UV2 values come from UV0 space.

## [0.8.1] - 2026-03-01

### Changed — Triangle surface projection transfer (replaces vertex-based)
- **Completely replaced vertex-based nearest matching with triangle projection**.
  For each target vertex: find nearest source TRIANGLE by point-to-triangle
  distance (filtered by face normal dot > 0.3), compute barycentric coordinates,
  interpolate UV2 from the 3 source triangle vertices.
- Triangle is atomic — all 3 vertices belong to same UV2 shell, eliminating
  seam ambiguity that plagued all vertex-based approaches.
- Includes full ClosestPointOnTriangle implementation (Ericson algorithm)
  with proper edge/vertex clamping for barycentric coords.
- UvTransferWindow now calls Transfer(targetMesh, sourceMesh) instead of
  Transfer(targetMesh, sourceShellInfos).

## [0.8.0] - 2026-03-01

### Fixed — 3-pass transfer: normal → 3D → UV0 disambiguation
- **Core insight**: one 3D vertex has multiple UV vertices on different shells
  (seam duplicates). Pure 3D nearest picks arbitrary seam duplicate → wrong UV2.
- **New 3-pass algorithm**:
  1. Normal filter (dot > 0.5) — separates front/back of thin walls
  2. 3D nearest among normal-compatible — spatial correspondence
  3. UV0 disambiguation — among source verts within epsilon of best 3D
     distance, pick the one whose UV0 is closest to target vertex's UV0.
     This correctly resolves seam vertices that share position+normal
     but belong to different UV shells with different UV2 coordinates.
- Logs UV0-disambiguated count for diagnostics.

## [0.7.9] - 2026-03-01

### Fixed — Normal-filtered 3D transfer (overlapping UV0 disambiguation)
- **Transfer uses normal filter + 3D nearest** instead of UV0 nearest.
  UV0 is overlapping/tiling on CementWall (front/back share same UV0),
  so UV0 nearest picks wrong matches. Normal filter (dot > 0.5) separates
  wall sides, then 3D nearest among filtered set gives unambiguous match.
  Falls back to unfiltered 3D nearest if no normal-compatible source found.

## [0.7.8] - 2026-03-01

### Fixed — Normal-filtered UV0 transfer (thin wall disambiguation)
- **Transfer uses normal filter + UV0 nearest**: for each target vertex,
  first filters source vertices by normal similarity (dot > 0.5), then
  finds nearest by UV0 among filtered set. Normal separates front/back
  of thin walls. UV0 gives precise position within same geometric side.
  Falls back to unfiltered UV0 nearest if no normal-compatible source found.

## [0.7.7] - 2026-03-01

### Fixed — Correct matching spaces for weld vs transfer
- **SourceGuidedWeld back to 3D nearest**: seam vertices have different UV0
  by definition, so UV0 matching gives them different source shell IDs →
  almost nothing gets welded. 3D matching works because normal check already
  prevents thin-wall confusion. Restores 26→18 shell reduction on CementWall.
- **Transfer stays UV0 nearest**: prevents copying UV2 from wrong side of
  thin geometry.

## [0.7.6] - 2026-03-01

### Fixed — UV0-space matching instead of 3D
- **Transfer uses UV0 nearest-vertex**: finds nearest source vertex by UV0
  distance, not 3D position. 3D matching fails on thin geometry (CementWall)
  where front/back faces are close in 3D but on different UV0 shells.
- **SourceGuidedWeld uses UV0 nearest**: determines source shell membership
  via UV0 proximity instead of 3D proximity. Same thin-geometry fix.

## [0.7.5] - 2026-03-01

### Fixed — Weld button runs source-guided weld
- **Weld button now two-phase**: Phase 1 = false-seam weld (pos+uv0+normal)
  for all meshes. Phase 2 = source-guided weld for target LODs — merges
  vertices by pos+normal when both belong to same source UV0 shell.
  Previously source-guided weld was hidden inside Transfer step only.
- **SourceGuidedWeld returns original** when nothing to weld (avoids
  unnecessary mesh copies).

## [0.7.4] - 2026-03-01

### Changed — Source-guided weld + 3D nearest-vertex transfer
- **Source-guided weld** (`Uv0Analyzer.SourceGuidedWeld`): merges target LOD
  vertices by position+normal only when both map to the same source UV0 shell
  via 3D proximity. Reunifies UV0 islands that LOD decimation split apart,
  while preserving intentional seams between different source shells.
  Fixes shell count mismatch (e.g. 7 source vs 26 target on CementWall).
- **3D nearest-vertex transfer**: completely replaces similarity-transform
  pipeline. For each target vertex, finds nearest source vertex by 3D position
  and copies UV2 directly. No UV0 shell matching dependency, no transforms.
  Eliminates UV2 out-of-bounds artifacts from degenerate/mirrored shells.
- **UI labels updated**: transfer report shows "3D nearest-vertex" method and
  "source shells used" count instead of matched/unmatched/mirrored.

## [0.7.3] - 2026-03-01

### Fixed — Mirrored shell transfer + degenerate shell fallback
- **Mirrored shells use source transform directly**: nearest-vertex matching
  gives wrong correspondences when target UV0 is reflected vs source. Now
  mirrored shells use precomputed `mirrorTransform` from source analysis.
- **Residual-based fallback**: if per-target-shell similarity transform has
  residual > 0.001 (degenerate small fragments), falls back to source's
  precomputed transform. Prevents tiny shells from producing wildly off UV2.
- **UV2 bounds diagnostic**: transfer now logs warning with exact bounds when
  any vertex UV2 falls outside 0-1 range.

## [0.7.2] - 2026-02-28

### Fixed — UV2 out-of-bounds on LODs + checker on all tabs
- **Per-target-shell nearest-vertex matching**: transfer no longer reuses source's
  precomputed UV0→UV2 transform directly on target UV0. Instead, for each matched
  target shell, finds nearest source vertex by UV0 distance and builds point pairs
  (target_UV0, nearest_source_UV2) to compute a fresh similarity transform per shell.
  Fixes UV2 coordinates going far outside 0-1 when LOD UV0 layout differs from LOD0.
- **Checker in toolbar**: moved checker toggle from Review tab to canvas toolbar,
  accessible on any tab (Setup, Repack, Transfer, Review).

## [0.7.1] - 2026-02-28

### Fixed — Weld persistence + checker after Apply
- **Weld persists through FBX reimport**: sidecar now stores `welded` flag per mesh;
  postprocessor calls `WeldInPlace()` before UV2 injection so vertex count matches.
- **FBX path lookup after weld**: added `fbxMesh` reference to MeshEntry so
  `AssetDatabase.GetAssetPath` works even after `originalMesh` replaced by welded copy.
  Previously welded meshes were silently skipped during Apply.
- **Checker preview after Apply**: `ToggleChecker` now falls back to `originalMesh`/`fbxMesh`
  if working copies were cleared by Refresh, checking for existing UV2 channel.

## [0.7.0] - 2026-02-28

### Added — UV0 analysis/fix + split padding
- **UV0 Analyzer**: detects false UV seams (weld candidates), degenerate UV triangles,
  flipped UV triangles within shells, and overlapping shell groups. Report displayed
  in Setup tab with per-mesh breakdown and color-coded warnings.
- **UV0 Weld**: merges false-seam vertices (identical position + UV0 + normal but
  different indices) by rebuilding index buffer and compacting vertex arrays.
  All vertex attributes preserved (tangents, UV1, colors, bone weights, submeshes).
  Operates on working copies only — FBX untouched.
- **Split padding**: shell padding (inter-island, passed to xatlas) and border padding
  (atlas edges, applied as post-repack linear inset). Border padding defaults to 0
  for Clamp mode lightmaps. Two separate sliders in Repack tab.
- `Uv0Analyzer.cs` — analysis + weld pipeline with spatial hashing for O(n) duplicate detection
- `RepackOptions.borderPadding` field, `XatlasRepack.ApplyBorderInset()` post-process

## [0.6.0] - 2026-02-28

### Added — Checker preview, FBX postprocessor, UV2 reset
- **Checker 3D preview**: procedural 8×8 colored grid with alphanumeric labels (A1..H8),
  applied to model in SceneView via UV2 channel. Temporarily swaps materials + meshes;
  fully restored on disable. Button in Review tab.
- **Apply UV2 to FBX (postprocessor)**: saves UV2 as sidecar ScriptableObject
  (`ModelName_uv2data.asset`) beside the FBX. `Uv2AssetPostprocessor.OnPostprocessModel`
  injects UV2 on every FBX reimport — identical to Unity's "Generate Lightmap UVs" approach.
  FBX file stays untouched on disk.
- **Reset UV2**: deletes sidecar asset and reimports FBX, removing all transferred UV2.
- `CheckerTexturePreview.cs` — procedural texture generation + material/mesh swap management
- `Uv2DataAsset.cs` — sidecar ScriptableObject storing per-mesh UV2 arrays
- `Uv2AssetPostprocessor.cs` — AssetPostprocessor that reads sidecar and injects UV2
- `CheckerUV2.shader` reads TEXCOORD2 for UV2 visualization

## [0.5.0] - 2026-02-28

### Changed — Architecture pivot: UV0-space shell transforms replace 3D projection
- **New transfer approach**: instead of projecting target vertices onto source mesh in 3D,
  compute per-shell similarity transform (rotate + scale + translate) from LOD0's UV0→UV2
  mapping, then apply the same transform to all LOD vertices via UV0 shell matching.
- xatlas repack preserves shell internal structure — only changes placement. This means
  the UV0→UV2 mapping per shell is a pure similarity transform (4 parameters: a, b, tx, ty).
- Shell matching uses UV0 bounding box overlap + UV0 centroid distance + 3D centroid
  proximity (disambiguates stacked/mirrored shells).
- Transfer is mathematically exact — no interpolation, no barycentric projection, no artifacts.
- Old 3D projection pipeline (SourceMeshAnalyzer, ShellAssignmentSolver, InitialUvTransferSolver,
  BorderRepairSolver) bypassed; code retained for reference.

### Added
- `GroupedShellTransfer.cs` — complete new pipeline: AnalyzeSource + Transfer
- `GroupedShellTransfer.ShellTransform` — similarity transform struct with Apply method
- `GroupedShellTransfer.SourceShellInfo` — per-shell data for cross-LOD matching
- `GroupedShellTransfer.ComputeSimilarityTransform` — least-squares similarity fit (UV0→UV2)
- Shell transform cache in UvTransferWindow (avoids re-analysis on repeated transfers)
- Review tab shows shells matched/unmatched + vertex coverage instead of triangle status bars

## [0.4.1] - 2026-02-28

### Changed
- BVH vertex projection is now primary UV transfer method (was fallback)
- Face-level bindings demoted to fallback when BVH finds no shell match
- Raised BVH projection weights (0.9 direct hit, 0.6 shell scan) to reflect higher reliability

## [0.4.0] - 2026-02-28

### Fixed
- Critical: vertex UV averaging across different shells producing stretched triangles spanning entire atlas
- Per-shell vertex accumulator now isolates UV contributions by shell ID — no cross-shell blending
- UV0 proximity used as priority signal for shell conflict resolution at shared vertices
- Post-validation pass detects and re-projects anomalous triangles exceeding shell UV bounds

### Added
- Target UV0 loaded in PrepareTarget for shell priority matching
- Source UV0 interpolation in InterpolateVertexUv and FallbackVertexProject
- Debug logging for vertex conflict resolution statistics

## [0.3.4] - 2026-02-28

### Fixed
- Repack now packs all selected meshes into a single shared UV atlas instead of repacking each mesh independently, preventing UV2 overlap when meshes share the same lightmap

## [0.1.0] - 2026-02-28

### Added
- xatlas native bridge (C++ DLL) with repack-only UV packing
- UV shell extraction via Union-Find connectivity
- UV overlap classifier with chart instance generation
- Full 7-stage UV transfer pipeline (shell assignment → initial transfer → border repair → validation)
- Triangle BVH for fast surface projection
- Border primitive detection and UV perimeter metrics
- Border repair solver with quality gate and conditional fuse
- Transfer quality evaluator with triangle status classification
- Editor Window with 8-stage pipeline control and per-stage re-run
- UV preview canvas with 7 visualization modes
- Multi-mesh atlas support (multiple renderers per LOD)
- Per-mesh quality reports
- CMake build system for native DLL (auto-fetches xatlas)
