# Tool modules and shared libraries

Each tool is an independently discovered `IUvTool` tab. Shared operations live in
libraries; dependencies point from tools to libraries and between libraries,
never from one tool to another tool.

`[MeshLabTool("stable_tool_id", MeshLabLibraries.Assets, ...)]` declares required
libraries. `ToolId` must match this ID. The registry resolves transitive library
dependencies before constructing a tool. Missing/disabled dependencies disable
only their consumers; cyclic dependencies and duplicate tool IDs are rejected.
All tools use the shared `mesh_view` library for universal 2D/3D inspection.

Project Settings → Mesh Lab → Tools & required libraries controls availability.
The hub's **Modules** button opens it. These are project-local runtime module
settings, independent of `Show Debug UI`; disabling a tab does not disable its
libraries. Libraries remain available for other consumers. This does not strip
code or native plugins from the editor assembly.

## Shared libraries and ownership

| Library | Shared API / implementation | Responsibility |
|---|---|---|
| `core` | `IUvTool`, `UvToolContext`, `MeshEntry` | Contracts and window-local data |
| `geometry` | `MeshAccess`, `MeshGeometry`, `TriangleBvh` | Mesh access and geometric queries |
| `uv` | `UvTopology`, shell helpers, xatlas | UV channels, borders and atlas operations |
| `vertex_channels` | `VertexChannels` | Attribute writes and change notifications |
| `mesh_view` | `MeshInspection`, `UvCanvasView`, `MeshViewport3D`, `UvLayer3D` | Shared projection, shading, inspection and overlays |
| `hierarchy` | `LodHierarchy`, `LodGroupUtility` | Hierarchy and LODGroup operations with Undo |
| `mesh_assets` | `context.Assets`, `FbxExport`, `SidecarStore`, `VariantExportPipeline` | Saving, export intents, sidecars and variants |
| `meshoptimizer` | `MeshSimplifier`, `LodPipelineOps` | Single-mesh simplification and batching into LOD levels |
| `uv_transfer` | `UvTransferWorkflow`, `UvTransferPipeline` | Reusable UV pipeline and its controls |
| `remesh` | `RemeshPipeline`, `RemeshNative`, `RemeshPreview` | Voxelization, unwrap and stage results |
| `baking` | CPU/GPU baker and BVH libraries | Baking and projection |
| `collision` | Collision-generation helpers | Collision geometry |
| `benchmark` | `SweepRunner`, `BenchmarkRunner` | Pipeline sweeps and reports |

LOD generation and individual mesh reduction use the same meshoptimizer library.
`LodPipelineOps` supplies the level ratios and adds grouping/LODGroup bookkeeping.
Meshes with attributes use `MeshSimplifier.Simplify`; indexed geometry before
unwrap uses `MeshSimplifier.SimplifyGeometry`. The latter retains the native
regularization/fold settings and error-only target (`targetTriangles = 0`).
Remesh is a consumer of simplification rather than its owner.

`UvTransferWorkflow` is a library component, not an `IUvTool`. UV Transfer is a
small discoverable adapter. Diagnostics owns a separate workflow instance and a
private canvas, so it can run without the UV Transfer tab or its callbacks.
Algorithm implementations remain in their existing geometry/UV libraries.

## Communication and lifecycle

- The hub supplies one context to the active tool. Call `context.Assets` for
  saving/export; never locate another window or cast another tool as a service.
- Use `IUvToolUvContent` and `IUvTool3D` to supply content to the common inspector;
  tools retain ownership of temporary stage meshes.
- `VertexChannels.Changed` invalidates shared caches after attribute writes.
- `IUvToolAssetLifecycle` provides optional before/after hooks for asset writes.
  The hub calls the active tool and restores common previews before writing.
- `OnDeactivate` releases static-event subscriptions and owned previews/resources.
  The hub also clears tool callbacks, custom content, fill modes and picking caches.
- Enabling/disabling another module preserves the active tool instance, results,
  callbacks and view state. Disabling the active module deactivates it first and
  selects an available tab. Inspection remains usable when all tabs are disabled.
- Generated LOD objects belong to the shared context. Export cleanup runs through
  `LodGroupUtility`, independently of the LOD tab and without static tool instances.
- Saved mesh assets are clones; disposing context-owned working meshes cannot
  destroy persisted output.

Keep public forwarding APIs when extracting old tool operations into libraries.
Do not add dependencies on concrete `*Tool` types to library code. New tools must
declare dependencies, use these contracts and cover their resource lifecycle.
`MeshLabModuleTests` verifies tab independence, transitive availability, preserved
active state, headless workflow ownership, independent export and simplification.
CI also runs `Tools~/check_tool_dependencies.py` to reject concrete tool references
outside their owner, missing tool dependency declarations, and library code that
uses a top-level type declared under `Editor/Tools/` (a shared contract such as an
export intent belongs to its library's folder, not next to a tab).
