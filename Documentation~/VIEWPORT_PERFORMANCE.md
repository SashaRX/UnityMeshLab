# UV and 3D viewport motion

The UV canvas now submits persistent GPU meshes instead of emitting each triangle
and wire edge through `GL.Vertex3` on every repaint. Geometry stays in UV space;
pan and zoom change the draw matrix. This covers attribute/island/status/matching/
validation fills, validation overlays, wire, UV boundaries and shell highlights.
Existing colours, alpha, face limits, duplicate-edge blending and draw order are
retained. UV wire uses the same line rasterization as before.

Selection and filter changes replace their affected batch. Shell opacity is a
shader parameter, so dragging its slider retains the same geometry. The checker
shares one cached tile mesh across UDIM tiles, motion and opacity changes.
Dense batches prepare arrays on workers and upload on editor update; a pending
batch requests repaint when ready. A completed upload also invalidates cached 3D
UV textures, including fills that supply their own UV arrays.

Context cache invalidation, channel writes and canvas cleanup release cached
geometry. UV arrays are snapshots; in-place edits must notify through
`VertexChannels` or invalidate the context caches. Channel writes drop associated
shell topology and picking data as well. Lightmap atlas transforms reuse their
transformed arrays until the source UVs or scale/offset changes. Measurements below
cover warmed motion, not initial preparation, editor-thread snapshots or uploads.

## Shell correspondence and picking

Connected UV0 islands of the selected source LOD (normally LOD0) own the palette
IDs. LOD siblings in the same mesh group use the transfer's `vertexToSourceShell`
provenance when available. Otherwise, a worker matches up to 32 deterministic face
samples per target island against the source surface BVH, voting for the source
island. Matching does not depend on equal UV bounds, island order or surviving UV
extremes. Separate overlapping islands keep their own IDs; a split target island
can inherit its source island's color. The geometric fallback is approximate when
surfaces overlap; explicit transfer provenance takes priority.

UV fill and the scene's Shells3D mode consume the same correspondence. The scene
preview applies the complete prepared cohort together, skips meshes lacking the
chosen channel, and restores previous renderer meshes/materials when disabled.
Correspondence survives switching the displayed LOD, including a hidden source,
and rebuilds on context, source LOD, mesh, topology or provenance replacement.
Camera/object motion does not trigger matching. Standalone tool content without
a source group uses local connected-island IDs. Small scene previews (up to 2,048
vertices, with a small source) retain immediate preparation; dense previews use
workers. The current Viewer's antialiased point material, X-Ray policy, shared
highlight colors and model-relative preview coordinates are retained.
Transfer algorithms are unchanged.

UV picking queries a prepared triangle BVH, replacing the limited face scan and
bounding-box fallback. It finds faces beyond the old 6,000-face budget and rejects
empty parts of an island's bounds. Selected/hovered 3D highlights compact only
their referenced vertices on workers; each mesh/channel retains at most one mesh
for the selection and one for hover, releasing the previous hover mesh.

The 3D canvas retains analytic capsule antialiasing, near-plane clipping, subpixel
coverage, depth testing and its existing MSAA policy. It now:

- builds source wire ribbons in one worker stage, without uploading and reading back
  an intermediate line mesh; Unity snapshots/uploads remain on the editor thread.
  Surface vertex colours do not tint uniform wire; coloured line overlays retain
  their own endpoint colours in separate cache entries;
- uses explicit position/colour equality for segment deduplication, avoiding boxed
  `Color32` equality on Mono;
- keeps floor/axis ribbons across repaints and translates them with the content;
- reuses material property blocks and caches inspection shading availability;
- computes the line's pixel basis per vertex instead of a length-squared division
  in every blended fragment. Rounded ends and line coverage are unchanged;
- caches diagnostic edge source meshes and unused-vertex clouds. Lines use the
  same antialiased ribbons; constant-pixel point quads expand on the GPU instead
  of rebuilding camera-facing geometry on every repaint.

## Reproduction and measurements

`Tools~/ViewportPerformanceReplay.cs` is a standalone EditorWindow replay. Copy it
to a disposable project's `Assets/Editor`, reference this package through the
project manifest, then launch Unity with:

```text
-batchmode -force-d3d11 -projectPath <scratch-project>
-executeMethod ViewportPerformanceReplay.Run -viewportCsv <absolute-output.csv>
-logFile <absolute-editor.log>
```

Run the same replay against baseline `4fdccda6` and the candidate package. It uses a
316 x 316 grid (199,712 triangles), a 1024 x 768 viewport, five-second warmup and
96 measured repaints with matching motion. The four modes draw status fill plus UV
wire, a 3D surface with full antialiased wire/grid/axes, shell fill with continuously
changing opacity plus UV wire, and the same 3D scene with 10,000 diagnostic points
and 10,000 diagnostic lines. The replay waits for pending draw buffers and the full
wire upload before accepting samples. The baseline uses its production transient
point/line path; the candidate uses cached source meshes.

Observed on 2026-10-15: Windows, Unity **6000.2.6f2**, **D3D11**, Built-in render
pipeline, NVIDIA GTX 980 Ti. Baseline already includes analytic line AA.

| CPU repaint submission | Baseline median / p95 (ms) | Candidate median / p95 (ms) |
| --- | ---: | ---: |
| UV pan and zoom | 64.3084 / 65.4345 | 0.0185 / 0.0273 |
| 3D orbit and translation | 0.3494 / 0.4858 | 0.1742 / 0.2415 |
| Shell fill and opacity | 36.5346 / 37.4649 | 0.0180 / 0.0246 |
| 3D + diagnostic points and lines | 9.3596 / 13.8858 | 0.2011 / 0.3043 |

These are warmed **CPU submission** costs, not total frame time or isolated GPU
timings, and do not establish FPS for a consumer project. Dense analytic wire still
costs GPU time. The UV cache trades memory for CPU work: about **24.45 MiB** of
uploaded batches in this example. The 3D wire/grid buffers take **96.22 MiB** versus
**99.64 MiB** at baseline (`Profiler.GetRuntimeMemorySizeLong`). Temporary build
arrays add to peak memory. The managed allocation counter returned only zero on
this Mono runtime; its CSV cells are blank, not evidence of allocation-free drawing.

Local CSVs, XML results and editor logs are under ignored
`_results~/viewport-performance/`; the replay source is tracked for reproduction.

## Regression coverage

`ViewportPerformanceTests` checks batch reuse across motion, release on replacement
and invalidation, translated floor/axis reuse, lightmap UV transforms, inspection
availability after vertex-channel writes, and pixel comparisons against immediate
UV drawing at different pan/zoom values. It also checks selection, validation filters
and UV edits update cached pixels. Existing `PreviewLinesTests` exercise sloped and
subpixel lines, near-plane clipping, occlusion, duplicate segments, uniform versus
vertex-coloured lines and target resolve. The colour-isolation fixture waits for
the restored SRP instance before the following synchronous lighting tests.
`ViewportElementsTests` covers source-connected LOD colors despite reordered or
changed target UVs, explicit provenance, hidden-source correspondence retention,
UV-write invalidation, worker preparation, eventual dense 3D fill, exact UV picking,
compact highlight lifetime, point buffer lifetime/pixel size, and shell/checker
blending against immediate drawing. `PreviewLodSwitchTests` also checks scene
preview completion with mixed channel availability and mesh restoration.

At candidate `abf58c20`, after rebasing onto main `4fdccda6`, expanded Built-in
and URP 17.2 runs on Unity
6000.2.6f2 / D3D11 each executed **406 tests**: Built-in **383 passed, 15 skipped,
8 failed**; URP **385 passed, 13 skipped, 8 failed**. All eight save/import
mesh-identity lifecycle failures reproduce with the same messages on unchanged
main in this scratch project; they are not new viewport regressions. The skips
cover 11 optional private transfer/FBX fixtures, an imported-model fixture and
Android DXT5nm packing, plus two URP integration cases in the Built-in run.
The expanded suites include current material inspection, topology, overlay/X-Ray,
model-relative coordinates and LOD lifecycle tests. Relevant viewport regressions
and URP integration with asset MSAA Off/2x/4x pass. C# compilation passes with and
without the FBX exporter define; identifier and tool-dependency guards pass.
The post-review focused URP run passes 19 tests, including a point source destroyed
before its deferred snapshot and stale transfer provenance on a small scene preview.
A later Built-in follow-up passes eight channel/cache tests. A scratch replay with
the readiness deadline forcibly expired after the first accepted sample completes
all four phases and 96 samples per phase; ready buffers do not time out during
measurement. The timeout remains active while required buffers are pending.
