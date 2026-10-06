# Resource lifetime review — 2026-10-06

Baseline: `ad5d9a0` (1.1.23). This is a focused ownership review of the preview
and baking paths named in the supplied audit, with runtime regressions for the
confirmed findings. It is not a whole-editor memory-profiler certification.

## Corrections to the supplied audit

`HideAndDontSave` objects require explicit destruction by their owners. They
are not reclaimed as native Unity resources just because their managed wrapper
becomes unreachable. A cache bounded to one object per managed domain still
needs teardown before that domain is lost.
[Unity 6 API documentation](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/HideFlags.HideAndDontSave.html)
shows explicit destruction and explains that UnloadUnusedAssets does not unload
these objects.

The source does allocate NativeArray buffers: `Editor/Mesh/MeshAccess.cs` uses
temporary native buffers and read-only MeshData. Their `using` scopes provide
cleanup. Absence of Marshal.AllocHGlobal or GCHandle is not evidence that the
C++ plugins allocate no memory; ownership of their returned handles must be
checked separately.

## Confirmed findings and fixes

- `VertexColorBakingTool.previewMaterial` and
  `PrefabBuilderPreview.vertexColorMat` had no explicit destruction. Deactivation
  now restores previews and destroys those materials; repeated teardown is safe.
- `CheckerTexturePreview` retained a material and 1024² texture;
  `ShellColorModelPreview` and `UvPngWriter` retained materials. The safety guard
  previously restored scene assignments before reload but did not release those
  cached objects. Reload/quit now restores scene references and destroys the
  resources; ordinary preview switching can keep the session caches.
- Vertex Color and Prefab Builder clone cleanup only looked at the live
  MeshFilter's current sharedMesh. Deleted scene objects therefore stranded their
  clones; replacing sharedMesh could destroy somebody else's mesh instead.
  Owners now track clones directly and destroy their own lists, independently of
  the scene object's continued existence. Partially activated previews are also
  cleaned up.
- CPU vertex AO and face-area correction destroyed readable BVH copies only at
  the successful end of the method. Cancellation, early return and exceptions
  skipped cleanup. Finally blocks now release the BVH copies and the current
  target copy on every exit.
- MeshAccess.ReadableCopy allocated its destination before reading/importer
  fallback. An exception abandoned that mesh. The allocation now has an outer
  catch that destroys the partial destination before rethrowing.

These are actual resource lifetime defects, not GC-only hygiene. The rendering,
AO sampling and quality calculations were not changed.

## Verification

Isolated Unity 6000.2.6f2, D3D11, using the published native DLL from 1.1.23:

- Three repeated checker/shell/PNG cache creation and reload-handler teardown
  cycles verify that captured native object references become destroyed and
  original renderer material/mesh assignments are restored. Repeated teardown
  and subsequent recreation are covered.
- Four preview cycles per case cover both tools and both scene-object deletion
  and external sharedMesh replacement. Owned clones/materials are destroyed;
  source, replacement and original materials remain alive.
- Injected CPU AO/correction failures after unreadable copies exist, CPU AO
  cancellation, and readable-copy failure check native Mesh object counts.
- The previous baseline fails three of those resource regressions: each CPU
  failure retains a mesh; five readable-copy failures retain five meshes.
  Fixed tests retain no additional Mesh objects.
- 211 targeted tests passed, including the new regressions, mesh access,
  compatible viewport checks, shader imports, AO blur and Remesh baking.
- Licence-free C# compilation passes with and without the FBX exporter define;
  identifier and tool dependency guards pass.

Local logs and XML are in ignored `_results~/resource-lifecycle/`.

## Existing test-environment failures

The initial broader targeted run was 92 passed / 2 failed. Both failed tests
also fail on unmodified `ad5d9a0`:

- `MeshViewport3DTests.AttributeColorsRenderIntoUvLayoutUsingTheSameEncodingAs3D`
  expects approximately 0.5 while this URP project reads approximately 0.737
  after color-space conversion.
- `MeshViewport3DTests.InspectionPreservesFourComponentUv7AndTangentAndColorAlpha`
  expects a decimal point while this editor formats the summary with a decimal
  comma.

The final 211-test selection omits these two known environment-sensitive
assertions and includes the remaining selected viewport checks. No production
rendering code, CI test settings or exclusions were changed to hide them.

## Reviewed paths without new changes

MeshViewport3D destroys its temporary meshes and all five materials in Dispose.
SourceAoBaker.Gpu constructor failures dispose allocated buffers and its compute
path drains submitted readbacks. GpuBvh query methods await their readbacks
before returning to owners; Dispose releases query and BVH buffers.
VertexAOBaker.Gpu tracks readable copies, unregisters its update callback and
releases buffers/copies in Cleanup. RemeshNative and MeshSimplifier release
published native mesh handles in finally, and the C++ remesh bridge uses local
RAII ownership before publishing those handles.

These code-path checks do not quantify driver memory residency or prove the
absence of leaks in every plugin, graphics backend or live-editor workload.
