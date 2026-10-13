# Preview update events

The hub retains the chosen Checker, Shells3D or Lightmap mode, preferred UV
channel and camera/UV navigation state. Scene overrides and GPU textures are
derived data: rebuilding them must not change those choices.

The shared **Surface** dropdown also offers **Unlit / Albedo**, **Unlit / Normal
map**, **Unlit / Gloss**, **Unlit / Metalness** and **Unlit / AO**. These modes
ignore the preview lights and the Lit toggle. The chosen mode persists across
models, LODs and UV/3D switches; existing serialized shading values retain their
numbers. Checker/Lightmap scene overrides remain a separate choice: material
channel inspection resolves the authored materials beneath those overrides.

Channel values are bound per submesh every frame, without cloning or editing
authored materials. Albedo shows the base texture and tint; Normal map shows
decoded tangent-space RGB with the material's normal strength (distinct from
the mesh's **Normals** attribute mode). Gloss means smoothness: packed metallic
or specular alpha, optional albedo alpha, or the material scalar. Metalness reads
packed red or the scalar; a specular workflow is nonmetallic. AO reads occlusion
green and its strength. Missing maps use the material's scalar, neutral normal
or white AO. Data channels display numeric values consistently in Gamma and
Linear projects. Surface opacity also works for channel inspection.

Standard and URP/Lit use their keywords and shared base-map UV transform. Custom
shaders use common texture/scalar names and each texture's own transform; this
does not evaluate arbitrary Shader Graph logic, custom packed-channel recipes
or layered materials. In the UV view, channel maps are sampled with UV0 while
triangles are placed through the selected layout channel. This lets UV1 layouts
show the authored material correctly. Preview-owned materials are released on
window teardown, alongside the existing viewport resources.

`CollectCanvasEntries` observes both the identities of displayed entries/meshes
and `UvToolContext.PreviewCacheVersion`. Identity changes cover replacement
outputs; the version covers edits to the same mesh object. When either changes,
the hub clears stale UV snapshots, topology/Spot state, inspection and viewport
resources, reapplies the selected mode and recollects current material references
before rendering that frame. An unchanged frame reuses its preview resources.

## Event matrix

| Event | Update path | State retained / lifetime rule |
|---|---|---|
| Completed or repeated Transfer | Nested preview suspension restores renderer references before releasing the previous output; `ApplyTransferResult` assigns the result and clears context caches | Preferred channel returns when generated UV2 becomes available; current output drives both UV and 3D |
| Repack, weld, symmetry preparation | Suspend overrides and detach renderer references to working meshes before mutation; resume the selected mode after the outer operation | Original UV0 remains the display source when that channel is selected; camera/pan/zoom are retained |
| Full Pipeline / auto-tune retry and winner selection | Preview stays suspended across asynchronous stages; restore the selected source atlas, dimensions and target transfers together, then invalidate caches | Release discarded attempt meshes and snapshots; no renderer points to a destroyed output during editor updates |
| Generated-mesh replacement | Displayed mesh identity and context version invalidate derived preview data | Keep navigation and the selected display mode |
| UV/vertex/triangle edits on an existing mesh | `VertexChannels.Changed` advances context caches, invalidates viewport/layer resources and drops transfer transforms/cross-LOD hints for that source mesh | Callers must raise the event after direct mesh edits; identity alone cannot detect them |
| UV channel change or missing-channel fallback | `OnPreviewChannelChanged`, `RestorePreferredChannel`, `EnsurePreviewChannel`, then all-mode reapplication | Preserve the user's preferred channel through temporary fallback |
| Source or preview LOD change | Content key and queued reapplication use the newly collected entries | Restore the previous LOD's authored mesh/material references; preserve camera and UV pan/zoom |
| New model or direct context refresh | `BeforeRefresh` restores old preview/working meshes before clearing entries; `OnMeshEntriesChanged` schedules reapplication | Never capture a temporary preview mesh as the FBX baseline |
| LOD renderer replacement/reordering with unchanged counts | Renderer identities and LOD slots are checked by `RefreshLodStructure` | Refresh even when renderer and LOD counts remain equal |
| Undo/Redo | Restore preview and working references before rebuilding context, then reapply | Retain selected mode and navigation state |
| Save mesh assets, apply UV2, export/overwrite FBX or vertex colors/channels | Nestable `PreservePreviewDuringWrite` scope; `BeforeWrite` suspends overrides; outermost `WriteFinished` resumes them | No temporary preview mesh/material enters a write; early returns, cancellation and exceptions also resume; repaint during a write cannot reattach an override |
| Reset working copies, UV2-from-FBX, selected sidecar or pipeline state | Restore old references before destroying working meshes or reimporting; retain mode through reset/write completion | Reset data, preserve navigation; existing destructive-action confirmation remains |
| Lightmap index, scale/offset, texture replacement or texture pixel update | Lightmap metadata, texture identity and `Texture.updateCount` participate in the content key | Rebuild from `ctx.DMesh`, not `MeshFilter.sharedMesh`, which may contain a temporary override |
| Entering Unity's preview scene | `MeshViewport3D` snapshots the authored scene's lightmaps before `BeginPreview`; the UV layer receives that snapshot | The isolated preview scene has an empty lightmap list; do not cache it as the model's atlas |
| Multiple renderers sharing a mesh | UV-layer render textures are keyed by mesh **and entry** | Each queued draw retains its renderer's atlas; shared mesh identity does not imply shared lighting |
| Drawing a lightmap overlay | Apply the renderer's scale/offset when sampling UV2 in `UvOverlay` | Identity transform for checker, shell highlights and other overlay calls |
| Disable/reload or switch tool/model | Existing teardown restores overrides, unsubscribes callbacks and releases derived resources | No temporary resources survive their owner |
| Content bounds change | `FrameIfRequested` updates content radius; the camera's far plane includes current bounds, including content away from the retained pivot | Axis scale and zoom limits follow the new size; camera position, orbit and distance remain the user's choices |
| Explicit Frame/F/Fit/manual navigation | Existing viewport/canvas input path | These are the actions that change framing; data refresh does not frame automatically |

The public `DrawTextured` and `RenderUvLayer` signatures remain available.
Additional atlas/snapshot arguments are internal. No serialized field, native
ABI, package dependency or asset-writing algorithm changes in this correction.

## Regression evidence, 2026-10-09

- Before the refresh correction, the new initial matrix failed **39 of 68**
  tests on baseline `59d634d`; the retained 28 older tests passed. Actual native
  transfers reproduced stale scene UV2, not just cache-key mismatches.
- The final preview matrix passes **104/104**, including repeated native
  transfer, original/repacked/transferred mesh replacement, same-instance edits,
  all three modes, UV/3D layouts, missing channels, resets, Undo, same-count LOD
  structural changes, nested/failed/successful writes and preserved navigation.
- GPU pixel tests reproduce and check two atlas regions and two differently lit
  instances of one mesh. They also exposed the empty lightmap list inside
  `PreviewRenderUtility`; the authored-scene snapshot fixes that render path.
- Three additional regressions failed on `08e13d4` before the content-bounds
  correction: stale zoom limits for small/large models and clipped distant
  content with an unchanged camera. They now pass, including GPU pixels.
- The affected Built-in subset passes **350 tests, 0 failed, 1 skipped**
  (351 total), Unity **6000.2.6f2 / Direct3D 11**, in an isolated consuming
  project. The skipped line-render integration test requires URP.
- A separate isolated **URP 17.2.0 / DX11** project passes **114/114** preview
  and line-render tests, with no skips. This includes the previously skipped
  integration fixture and the new atlas/instance GPU pixel assertions.
- Both C# reference-assembly builds pass, with and without
  `LIGHTMAP_UV_TOOL_FBX_EXPORTER`.

Local logs, XML results and compiler output are in the ignored directory
`_results~/preview-refresh-20261009/`. Test inputs are synthetic working meshes;
FBX/write fixtures create owned temporary assets in the isolated project. These
checks cover the listed event paths, not arbitrary plugins that mutate a mesh
without notifying the context, and do not certify UV transfer quality on every
model. The user's source project and FBX/sidecar files are not test outputs.
