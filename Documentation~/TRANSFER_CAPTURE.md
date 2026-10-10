# UV transfer capture and replay

Use this when a transfer stretches UV2, assigns a mirrored instance to the wrong
source, or leaves overlapping triangles. Capture is opt-in and writes outside
`Assets`, under `<Unity project>/BenchmarkReports/transfer_<UTC>_<id>/`.
It does not save mesh assets, sidecars or FBX files.

## Capture the failing run

1. Open Mesh Lab and select the LODGroup. Keep the settings that produce the issue.
2. Enable **Capture next Full Pipeline / Transfer run** in Diagnostics, or invoke
   **Tools > Mesh Lab > Diagnostics > Capture Next Transfer Run**.
3. Run Full Pipeline. Alternatively, **Run Captured Full Pipeline** uses the current
   UV Transfer workflow and settings. To isolate matching against an already packed
   source, arm capture and run Transfer only.
4. The Console prints the capture directory. Preserve the whole directory, including
   `manifest.json`, `meshes/` and `details/`. The manifest alone cannot replay.

**Capture Current Transfer State** exports the current working state for inspection.
It cannot recover historical matching hints or earlier pipeline stages; this snapshot
has no replayable transfer pairs. Use next-run capture for a reproducible case.

Full-pipeline captures include imported meshes, meshes after analysis/weld, every
auto-tune attempt after symmetry/repack/transfer, and the selected final state.
Attempt labels include the actual symmetry separation. Context/workflow settings,
material texture sizes and tiling, renderer transforms, package provenance and Unity
version accompany the snapshots. A partial or cancelled run is labelled explicitly.

Schema 2 keeps the manifest below the shared 64 MiB writer/replay limit. Large
stage metrics, transfer results, hints, traces and per-triangle validation live in
checksum-protected `details/<SHA256>.json` files, each limited to 512 MiB. Replay
loads pair details on demand. Older schema 1 captures are still supported.

Each completed transfer pair stores the exact source UV0/UV2, target geometry/UV0,
cross-LOD hints passed to that invocation, lightmap dimensions and clamp setting.
Binary snapshots preserve float bits, UV channel dimensions and submeshes and are
named by SHA-256. Matching traces contain the best eight **evaluated** candidates
per search, actual distance/normal scores, initial/final source assignment, hint and
fragment use, fallback method and source transform residuals. They do not evaluate
extra candidates or change matching. Shell traces are capped at 10,000 shells;
`truncated` marks the cap.

## Replay

Captured pairs can also be compared across transfer methods with the
[autonomous benchmark](TRANSFER_COMPARISON.md), including a batch entry point,
repeated timing runs and independent geometry/overlap metrics.

Choose **Replay Last Transfer Capture** or **Replay Transfer Capture File**.
Replay uses owned temporary meshes and leaves the scene and source assets untouched.
It uses the currently loaded transfer code with each captured pair's exact inputs.
It does not rerun weld, symmetry split or repack: their stage snapshots locate where
the defect first appears; the repeated worker run isolates transfer itself.

`replay_<UTC>_<id>/replay.json` records:

- `baselineEqual`: the replay UV2 hash matches the captured output.
- `changedVertices`, `maximumUvDelta`, `changedMappings`: differences from baseline.
- `repeatEqual`: a second run, with tracing disabled, has the same UV2 and mappings.
- Per-pair errors and a `details` checksum referencing geometry-based quality, the
  complete transfer result and a new matching trace.

The report contains summaries only, with the same 64 MiB metadata limit. Each pair's
bulk data is saved to `replay_<UTC>_<id>/details/<SHA256>.json` (512 MiB per payload)
before the next pair is processed; the report does not retain the arrays from prior
pairs. Keep the replay folder together when sharing it. Read a payload with the
same checksum validation used for capture detail files.

Invalid checksums, unsafe blob names, unsupported formats and missing inputs are
rejected. A current-state snapshot requires a new captured run before Replay works.

All menu entry points are callable through Unity MCP `execute_menu_item` with the
same complete menu path. No MCP-specific assembly dependency is required.

## Rectangular source textures

For a 1024×2048 source texture, the area-preserving pre-pack metric scales U by
`sqrt(1/2)` and V by `sqrt(2)`. This removes the texture aspect from correctly authored
source UVs before ARAP and packing. Material tiling contributes to the metric.
The original UV0, including extra UV components, vertex format, stream and raw bytes,
is restored before transfer. Float16 channels retain their half precision layout
and bit patterns; the newly generated UV2 is retained separately.
The shapes of islands still follow geometry; a rectangular surface should retain a
rectangular island.

The native bridge returns coordinates normalized independently by packed width and
height. A rectangular native atlas is converted to a square UV2 domain by multiplying
its coordinates by `(width/max(width,height), height/max(width,height))`. Transfer
receives the corresponding square dimensions. This does not require native changes.
Capture records both native packed dimensions and effective lightmap dimensions.
Border padding is applied after the square-domain conversion, against the requested
lightmap resolution, so the shorter native axis retains the configured margin.

Meshes with conflicting material texture aspects or missing texture metadata on
any used material receive no mesh-wide source aspect correction and a warning;
individual texture dimensions and unresolved materials are still captured. Empty
submeshes do not contribute a material metric.
No texture metadata means no source correction. These cases require inspection of
the captured material/submesh data before introducing a per-material algorithm.
One-component UV0 is preserved without applying a 2D source metric. Working copies
and binary snapshot restoration also retain one-component auxiliary UV channels.

## Reading quality

`worstAnisotropy`, `areaWeightedAnisotropy`, and `faceAnisotropy` compare singular
values of the UV mapping against transformed 3D triangles. 1 is isotropic, 2 means
twice the density along one axis. Compare raw UV0, UV0 in the texture pixel metric,
and square-domain UV2. Uniform texel density or equal UV area alone does not establish
isotropy. Degenerate and invalid triangles have separate counts.

Overlap diagnostics count positive-area triangle intersections, including pairs
within the same shell/source. Shared edges are legal. The scan stops at 2,000,000
candidate comparisons; `overlapScanComplete=false` means counts are lower bounds.
`overlapPairArea` is a sum over pairs, not union area. These diagnostic counters do
not change symmetry/overlap acceptance, repair or auto-tune selection.

## Verification

Unity 6000.2.6f2 / DX11: 340 EditMode tests passed, none failed, one skipped across
transfer capture, preview, native xatlas, geometry helpers, mesh access, shell
extraction, topology, normals, remesh bake, source-normal scale, hierarchical repack,
FBX metrics, UV chart repair/merge and atlas diagnostics. The rectangular-texture
case runs with ARAP and texel normalization disabled: correction yields isotropic
UV2 after repack and transfer; disabling source correction retains approximately
2:1 axis stretch. UV0 and its extra components remain unchanged. Exact capture/replay
includes two LODs and nonempty cross-LOD hints, and verifies tracing does not change
the result. This verifies synthetic fixtures; a user's failing model still needs a
next-run capture before declaring its symmetry/overlap issue fixed.

The skipped source-normal convention fixture requires a URP source shader absent
from the isolated test project. Capture regressions verify settings from the derived
UV Transfer tool, single-bit UV differences and signed zero, scalar UV channels in
readable/unreadable working copies and snapshots, legacy replay, payload checksums,
capture errors and stage diagnostics exceeding 64 MiB without growing the manifest.
Source-metric regressions cover textureless used materials and final pixel borders;
shell extraction retains historical IDs. Replay file I/O is asynchronous.
After reviewing the Sonar autofix, the affected subset (transfer capture, shell
extraction, geometry helpers and mesh access) passed 42/42 tests without skips.
This is a subset of the 341-test integration run above. Both FBX compile variants
also passed on the reviewed code.
The later preview/UV0-format/replay-report changes passed 138/138 tests without
skips on Unity 6000.2.6f2 / DX11: transfer capture, mesh access, FBX export, preview
LOD switches and the 3D viewport. The added regressions verify Float16 UV0 formats
and raw bytes through readable/unreadable copies and native repack/transfer, and
replay pair payloads exceeding 64 MiB in aggregate with a compact summary report.
