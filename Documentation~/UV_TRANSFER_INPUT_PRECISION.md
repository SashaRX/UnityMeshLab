# UV transfer input precision

UV Transfer reads Unity-imported mesh sub-assets. It does not parse source FBX
geometry directly. Raw MeshData copying preserves the imported buffer's precision;
it cannot recover bits already lost to importer compression.

Explicit UV stage actions now check every included model input. Before Analyze,
meshopt, Weld, Symmetry, Repack (combined or per mesh), Full Pipeline, or Transfer
(all targets or one LOD), imports with Mesh Compression, mesh optimization or
importer welding enabled are prepared again. The importer settings persist as
Compression Off, optimization None and Weld Vertices off. Normals, tangents,
scale, triangulation and Read/Write settings are preserved. In-memory meshes and
ordinary .asset meshes do not use ModelImporter and are left alone.

Preparation suspends the existing preview, releases working geometry, batches
imports and resolves fresh sub-assets by local file ID, with a unique-name fallback
when the importer regenerates IDs. Entries retain their identity, inclusion and
selected LOD. Excluded entries sharing an imported file are rebound too. Old
sidecar geometry/UV replay is bypassed for this import without deleting the sidecar.
Ambiguous or failed rebinding stops processing. Generated atlases, transfer results,
analyze reports and matching/preview caches are invalidated before they can be reused.
Clean subsequent stage calls do not reimport or discard completed work.

If Transfer itself first discovers unsafe import settings, preparation runs but
Transfer stops with an instruction to rerun Full Pipeline, or repeat the intended
Weld/Symmetry and Repack stages. Silently projecting into a discarded atlas would
combine two different input topologies. Full Pipeline prepares before starting
its diagnostics capture and continues automatically through the enabled stages.

Validation uses a real ASCII FBX fixture independent of the optional FBX SDK.
High Compression introduces position error 0.00143937 and UV0 error 0.00153508
on this fixture. Prepared input matches the Compression-Off import exactly at
every triangle corner; original FBX bytes remain unchanged. Tests cover all four
compression levels, owned-result disposal, clean repeated calls, excluded entries,
Read/Write-off Analyze, individual stages, Transfer invalidation and Full Pipeline.
The units and error magnitudes belong to this fixture and are not universal bounds.

This removes importer quantization from these prepared inputs. It does not restore
precision already lost in the authored FBX, disable user-selected tool meshopt/Weld
stages, provide double-precision Unity vertices, or certify all transfer charts.
