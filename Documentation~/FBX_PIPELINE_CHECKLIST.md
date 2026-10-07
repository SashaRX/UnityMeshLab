# FBX pipeline rules

Regression checklist for FBX authoring + tooling. Each violation
maps to a category in `patch_fbx_materials --report-file` and / or
a Unity import warning. Better to not create the violation than to
patch it after the fact.

This document is the source of truth — `patch_fbx_materials` only
exists to catch what slipped through.

## 1. Materials

| Rule | Why |
|---|---|
| Class is `FbxSurfacePhong`, not Lambert / generic `FbxSurfaceMaterial` | Max FBX importer maps Phong → Physical Material BaseColor reliably; Lambert loses the diffuse-texture link during auto-conversion |
| `sDiffuse` is wired to a real `FbxFileTexture` (not just RGBA color) | Without an attached texture Unity renders the material pink |
| Both `FbxFileTexture::SetFileName()` (absolute) and `SetRelativeFileName()` populated | Absolute paths break on every other machine; relative is the fallback |
| Material name `M_<NameSpec>`; texture name `T_<NameSpec>_<Suffix>` (`_Albedo` / `_BaseColor` / `_AlbedoTransparency` / …) | `--guess-textures` only resolves textures that follow this convention |
| **No placeholder materials** (`Lit`, `Default`, `Material`, empty name) | Unity surfaces them as `None (Material)` in the Remapped Materials list |
| No `FbxLayerElementMaterial` with `mapping=eNone` on layer 0 | Max reads layer 0 first and treats `eNone` as "no material", silently dropping the assignment |
| `FbxFileTexture` object name = basename of file (no extension) | Max FBX importer rewrites every texture name to a generic channel label (`DiffuseColor_Texture` etc.) — losing the human-readable identity |
| `FbxNode::SetShadingMode(eTextureShading)` on every node with materials | Without it Unity renders the mesh unlit / wireframe |

## 2. Triangulation

| Rule | Why |
|---|---|
| `FbxGeometryConverter::Triangulate` only on **static** meshes (no `FbxSkin` / `FbxBlendShape` / vertex cache) | Triangulation reorders polygon-vertex indices; deformers store CP indices, mismatch → broken skin/blendshape |
| After triangulation, `FbxLayerElementMaterial::mapping=ByPolygon` must remain `ByPolygon` (not collapse to `eAllSame`) | SDK occasionally loses mapping mode → Unity sees "no material" |
| No degenerate polygons: 3 collinear control points, duplicate vertex indices in the same polygon, or polygon size < 3 | Source of black flickering and lightmap artifacts in Unity |
| Run `mesh->BuildMeshEdgeArray()` after retriangulation if you ship smoothing groups | Max reads edge data for smoothing |

## 3. Layers — UV / vcolor / normals / tangents

| Rule | Why |
|---|---|
| **Never clamp UV0** | Destroys every tiled-texture pattern |
| UV1 (lightmap) clamped to `[0, 1]` | UV1 outside range breaks lightmap pack |
| UV2 — clamp X to `[0, 1]`, Y per project convention (this codebase: 0) | UV2 is metadata (instance-index / wind / scaler) |
| No empty `FbxLayerElementUV` / `FbxLayerElementVertexColor` slots (eDirect with size 0, or eIndexToDirect with mismatched index array) | Unity / Max surface this as "Invalid UV index table" / "[LayerElement] Bad number of elements in array" |
| Each layer-element's `mapping mode` matches array size: `eByControlPoint→cpCount`, `eByPolygonVertex→polyVtxCount`, `eByPolygon→polyCount`, `eAllSame→1` | Mismatch means readers stuff arbitrary values into the gaps |
| Diffuse-texture `UVSet` name = name of `mesh->GetElementUV(0)` | Without it Max drops the texture-mesh binding (UV resolution fails) |
| `_COL` meshes (node name suffix `_COL`, case-insensitive) ship **no UV channels and no vcolor layers** | Collision meshes never render — pure dead weight |
| One vcolor layer per mesh on layer 0 | Multiple vcolor layers surface as unnamed map channels (4:map, 5:map…) in Max and confuse material setup |
| `FbxLayerElementNormal::mapping = eByPolygonVertex` if the mesh has smoothing groups, `eByControlPoint` if not | Max reads the two cases differently |

## 4. Vertex colors

| Rule | Why |
|---|---|
| If your shader reads `mesh.color`, ship a vcolor layer | Without one Unity reads (0, 0, 0, 0) → every multiply collapses to black |
| RGBA in `[0, 1]` — clamp at export | Unity doesn't validate; out-of-range values break shaders silently |
| One vcolor layer (layer 0) | See §3 — extras leak into Max as map channels |

## 5. Nodes / hierarchy

| Rule | Why |
|---|---|
| `FbxNull` is fine for dummy / pivot helper / hinge anchor | Unity HingeJoint in a prefab references the anchor by node name — don't rename or strip these |
| `FbxSkeleton` only when there's an actual skin attached | Orphan skeleton bones are pure overhead |
| No `FbxCamera` / `FbxLight` / NURBS / patches / IK effectors in game assets | Unity ignores them; they bloat the node tree |
| `_COL` suffix on collision meshes is required (case-insensitive) | The convention is what the patcher / Unity-side scripts key off of |
| **Never use generic mesh-attribute names** (`Scene`, `Geometry`, `Default`, empty) | Max FBX importer auto-resets every mesh attribute to `Scene` on round-trip; on the export side write `mesh->SetName(node->GetName())` so Unity sees stable identifiers |
| Empty leaf dummies (`FbxNull`, no children, identity transform) — strip before export | Junk left behind by XForm operations / hierarchy edits |
| Hidden nodes — strip *or* mark `_COL` (collision conventionally hides them) | Strip-hidden tooling defaults assume `_COL` is the only legitimate hidden case |

## 6. Skin / Bones

| Rule | Why |
|---|---|
| **Never reorder or remove control points** on a mesh with `mesh->GetDeformerCount() > 0` | Cluster stores CP indices; renumber → broken skin upload in Unity ("Skinned mesh VBO size does not match skin data") |
| Bones (`cluster->GetLink()`) must exist in the scene at export time | Orphan cluster = broken skinned mesh |
| `LclScaling` on bone nodes AND their ancestors stays as authored | The cluster's stored bind transform (`TransformLink`/`Transform`) is computed against the original world matrix; reset → mesh deforms in the wrong frame (huge mesh on tiny skeleton) |
| `Geometric{Translation,Rotation,Scaling}` on skinned meshes = identity | Geometric* applies before LclTransform and isn't propagated to children — guaranteed to break the rig |
| `BlendShape` target CP count == base mesh CP count | Mismatch → broken blendshape |

## 7. Scene-level

| Rule | Why |
|---|---|
| `FbxDocumentInfo` populated: `Original_ApplicationName/Vendor` AND `LastSaved_ApplicationName/Vendor` | Empty SceneInfo is the strongest signal a metadata-stripping tool re-saved the file. `--detect-modified` flags this |
| Scene unit = meters (or call `FbxSystemUnit::m.ConvertScene` at export) | Unity Scale Factor 0.01 breaks physics, prefab overrides, lightmap-scale, batching |
| After `ConvertScene`: bake the compensating LclScaling into mesh CPs | SDK puts `0.01` on root child — Unity sees it but downstream batching / physics doesn't |
| **Embed Media OFF** | Unity duplicates extracted textures into a temp dir on every reimport |
| No `FbxAnimStack`/`FbxAnimLayer` on static meshes | Max FBX exporter ships a default "Take 001" with zero-curve layers — pure overhead |
| No orphan vertices (CPs not referenced by any polygon) | They bloat the count, push the AABB outward, break Unity bounds + lightmap-scale heuristics |
| No degenerate polygons | Lighting glitches, lightmap artifacts |
| No nodes with negative-determinant accumulated scale | Unity reads inverted normals as backface-culled → mesh appears transparent from the front |

## 8. Naming

| Rule | Why |
|---|---|
| **ASCII only** in node / mesh / material / texture / layer names | Cyrillic / CJK / Hiragana / Katakana / Hangul break Unity Addressables, asset bundles, filesystem-naming rules |
| No Windows-illegal characters: `< > : " / \ | ? *` | Path errors |
| No ASCII control codes (< 32, == 127), no leading/trailing spaces or dots | Path / serialization errors |
| Texture file paths must NOT contain machine-specific roots (`E:\Evegoplayon\…`, `D:\Users\MAD\Downloads\Telegram Desktop\…`) | Dangling reference on every other machine. Use relative paths or a project-rooted absolute |

---

## 9. Tooling — what to use, what to avoid

### UnityMeshLab (this project's own tool)

[https://github.com/SashaRX/UnityMeshLab](https://github.com/SashaRX/UnityMeshLab) — a Unity Editor package
that exposes UV2 Transfer / Atlas Pack / UV0 Optimize / LOD Gen /
Collision (V-HACD) / Vertex AO via **Tools → Mesh Lab**. Output goes
through Unity FBX Exporter (`ModelExporter.ExportObjects`).

This means UnityMeshLab outputs are detected by
`patch_fbx_materials --detect-unity-modified` (creator =
`Unity FBX Exporter`). That category in the report is **not a
warning** for files that legitimately came from this tool — it's a
provenance marker.

**Compatibility rules with `patch_fbx_materials`:**

* UnityMeshLab maintains hierarchy nodes named `_LOD0` / `_LOD1` /
  `_COL`. Don't run the patcher with `--strip-non-mesh-nodes` or
  `--strip-empty-dummies` on UnityMeshLab outputs **before**
  Unity has reimported them — those passes can prune the
  scaffolding the postprocessor expects to see.
* UnityMeshLab's UV2 layouts are atlas-packed and may legitimately
  contain values **outside `[0, 1]`** when the packer overflows
  intentionally. Do not run `--clamp-uv2` on UnityMeshLab outputs
  unless you explicitly want to flatten the atlas.
* `_uv2data.asset` sidecars (next to each FBX) are the source of
  truth for UV2 + collision metadata across reimports. Don't
  delete them. The patcher doesn't touch `.asset` files.
* Vertex AO data is shipped as vcolor on layer 0. `--clamp-vcolor`
  is safe (data is already in `[0, 1]`); `--strip-extra-vcolor-
  layers` is safe (only one layer is generated). Do **not** run
  `--fill-missing-vcolor` on a UnityMeshLab output — the layer is
  already there with real data, the fill would be a no-op anyway.
* Run `patch_fbx_materials` AFTER UnityMeshLab, not in parallel.
  The patcher is a postprocess for cleanup of legacy / external
  FBX files; UnityMeshLab outputs are already clean for everything
  it cares about.

### TS_ plugin

A custom 3ds Max FBX rewriter (the `TS_UnityExport_SDK` plugin
this repo ships, plus its older predecessors). Round-tripping an
FBX through it can drop:

* `FbxDocumentInfo` (empty SceneInfo) — `--detect-modified` flags it.
* Vertex colors on edge cases.
* Custom material setups where a non-Phong class is involved.

The **current** version (TS_UnityExport_SDK Phase ≥3) is
non-destructive for the categories above; the in-the-wild files
that flag `EMPTY-SCENEINFO` come from older revisions. Reexport
from the original Max source if possible. If not, the patcher's
`--rebind-missing-textures` + `--restore-mesh-names-from-node` +
`--restore-texture-names-from-file` recover what they can.

### MeshLab (open-source desktop, NOT UnityMeshLab)

Different tool — `[meshlab.net](http://meshlab.net)`. **Do not use for FBX round-trip.**
It is fundamentally an `.obj` / `.ply` decimation tool with weak
FBX writers. Round-tripping through MeshLab will:

* Drop `FbxDocumentInfo` entirely.
* Leave orphan control points (decimation / welding residue).
* Create degenerate triangles (zero-area).
* Strip materials to Lambert or remove them.
* Invalidate skin / blendshape (CP renumber).
* Strip every `FbxAnimStack`.
* Reset mesh attribute names to `Scene` / `Geometry`.

If you need decimation, use **Max ProOptimizer / MultiRes** or
**Blender Decimate Modifier**. If you need geometry cleanup, use
**Max Edit Poly → Vertex Weld / Cap Holes** or **Blender Mesh →
Clean Up**.

### Unity FBX Exporter (used by UnityMeshLab internally)

Generally fine, but be aware that:

* Vertex colors are sometimes silently dropped (Unity-side bug).
* Custom-property bloat — every Unity GameObject component
  serializes string properties on the FBX node.
* Animation curves are quantized — not byte-exact round-trip.
* `Original_ApplicationName='Unity FBX Exporter'` — flagged as
  `UNITY-FBX` in the patcher report.

Use it only when you have a concrete reason to (UnityMeshLab
operations, prefab-to-FBX export). Never do
Unity-FBX-export → Unity-import → Unity-FBX-export → … cycles —
each round adds quantization error and custom-property cruft.

---

## 10. What `patch_fbx_materials` catches and how to read the report

Each category in `--report-file` output corresponds to a violation
of the rules above:

| Report category | Rule violated | How to fix at source |
|---|---|---|
| `EMPTY-SCENEINFO` | §7.1 | Reexport from Max / Blender; never round-trip through MeshLab desktop |
| `UNITY-FBX` | §9 (informational, not always a defect) | Provenance marker — file came from Unity FBX Exporter (often UnityMeshLab) |
| `CYRILLIC` / `CJK` / `ILLEGAL-CHARS` / `CONTROL-CHARS` | §8 | Rename in source DCC, reexport |
| `NEGATIVE-SCALE` | §7.8 | Reset XForm in Max, or apply transform in Blender, before export |
| `BROKEN-ALBEDO` | §1.3 + dangling-path texture refs | The source FBX has texture paths from another machine; reexport with relative paths or fix paths in Max Material Editor |
| `UNRESOLVED-ALBEDO` | §1.4 | Material name doesn't follow `M_<X>` / texture isn't `T_<X>_<Suffix>` — rename in Max or set up `--texture-root` to point at a richer index |

Unity console warnings → rules:

| Unity warning | Rule violated |
|---|---|
| "Skinned mesh VBO size does not match skin data" | §6.1 / §6.3 / §6.4 |
| "Invalid UV index table" / "[LayerElement] Bad number of elements in array" | §3.5 |
| Pink "missing material" | §1 |
| `Scale Factor 0.01` warning on import | §7.2 |
| Black mesh where shader expects vcolor | §4.1 |
| Mesh transparent from front (backface-culled) | §7.8 |

---

## 11. Authoring checklist (for new export paths in tooling)

Before merging any code that writes FBX:

1. `FbxDocumentInfo`: write Original_/LastSaved_ ApplicationName + Vendor.
2. Scene unit: write meters (or call `ConvertScene` and bake).
3. Materials: only `FbxSurfacePhong`, populate `sDiffuse`, attach `FbxFileTexture` with both abs and relative paths.
4. Layer elements: every UV / vcolor / normal layer has its array size matching its mapping mode. Empty layers are not shipped.
5. `_COL` meshes: no UV / no vcolor / no material if rendering-disabled.
6. Mesh names: copy from owning node, never `Scene` / `Geometry` / empty.
7. Skin / blendshape integrity: run `mesh->GetDeformerCount()` test; if non-zero, do not call any CP-mutating op.
8. Names: ASCII-only.
9. No `Take 001` empty animation stacks on static exports.
10. No `Embed Media`.

If the tool produces a file that flags ANY category in
`patch_fbx_materials --report-file`, the tool is wrong, not the
patcher. The patcher is the regression net, not the authoring
contract.

---

## 12. Isolated re-save (the only sanctioned in-tool path)

Every FBX-writing path in UnityMeshLab MUST go through the
single isolated-export core in
`Editor/Tools/LightmapTransferTool.cs`. There is no separate
"safe" pipeline parallel to the destructive one — the core
itself is the safe path, and "destructive" operations are
expressed as a wider `FbxExportIntent`.

### The contract

A re-save mutates ONLY the per-vertex channels listed in the
caller's `FbxExportIntent`. Everything else — node names,
hierarchy, transforms, material assignments, untouched UV
channels, vertex colors, normals, tangents — is inherited
byte-identical from the source FBX clone (modulo what the
Unity FBX Exporter itself rewrites at the FBX-document level;
see §9).

### Channel re-save in the FBX document (UV sets, vertex colours, normals, tangents)

An intent made only of `UV0`…`UV7` and `VertexColors` does not go through
Unity's FBX Exporter at all (`Editor/Assets/FbxChannelWrite.cs`):

* The source file is loaded with the FBX SDK and saved back in its own
  container format (binary/ASCII) and file version, with embedded media
  re-embedded. Two exceptions: a file older than FBX 7.1 is saved as 7.1
  (the oldest version the SDK writes), and the SDK rewrites the header's
  creator string and timestamps. Polygons (quads, n-gons), control points, smoothing,
  materials, nodes, properties and every other layer element are never
  rebuilt.
* Inside a written channel only the corners whose value changed are
  written; unchanged corners keep their stored doubles bit for bit. A
  by-control-point set stays by-control-point when the change allows it;
  otherwise it becomes per-corner indexed with the old values kept. New values
  share an entry only between corners of the same control point with the
  same value (eIndexToDirect, as TS_UnityExport_SDK writes it), so
  overlapping or mirrored shells are never welded on a DCC re-import.
* Vertex colours are read from and written to layer 0 only — the only
  colour layer Unity's importer reads (TS_UnityExport_SDK checklist I4).
  Written values are clamped to `[0, 1]` (§4); stored ones are left as they are.
* Which Unity vertex a corner became is recovered from a throwaway import
  of a tagged copy (corner index in an extra UV set, same importer settings,
  `Assets/__MeshLabTemp`, deleted afterwards, also when it fails). A mesh with
  all eight UV sets lends its last one to the tag; that set's values are read
  from the file per corner instead. `FbxCornerMatch` pairs corners with the
  working mesh by position (bit-identical: same file, same import), by the
  polygon's own corners, and by the untouched UVs and colours, so welding,
  vertex splits and triangle order do not matter.
* Refused, nothing written: a value seam inside one polygon (the polygon
  cannot hold it without being split), a UV set that would skip a channel
  (UV3 on a mesh with one set), corner tags lost on import (Mesh
  Compression), an instanced FBX mesh edited from more than one Unity mesh.
* The source importer is left alone, except `generateSecondaryUV` is
  switched off when UV1 was actually written (it would replace it). Since
  that holds for the whole model, every mesh's generated UV1 is then written
  too; a save that would leave a mesh of the model without UV1 (not loaded in
  MeshLab) is refused; untouched Read/Write-disabled meshes are read through
  `MeshAccess` for it. The sidecar's UV2 replay is held off only for an import
  that brings a written or removed UV1, and a persistent sidecar is updated to
  the saved UVs after the save (a removed UV1 drops its entry; the entries are
  built before the write, from the meshes the file was read with). An import
  that fails after the file is written is reported, and the save still
  finishes. A standalone renderer (no LODGroup) is relinked to its reimported
  mesh. A save of several files frees the working copies and reloads the scene
  once, after the last file, and not at all when a file holding unsaved work
  was not written (cancelled, refused, failed).
* "Unchanged geometry" means the same faces per submesh (position loops,
  same winding, any order or vertex numbering), not just the same counts.
* A channel the working mesh dropped (Cleanup's attribute removal) is removed
  from the file: the colour set, or the last UV sets. Removing a UV set before
  one that stays would renumber it and is refused.
* Normals and tangents are channels too (hub save): layer 0's normal, tangent
  and binormal elements take the changed corners, with each value mapped back
  into the mesh's control-point space through the map fitted to its import
  (`FbxSpaceFit`: normals by its transpose, tangent and binormal as directions;
  binormal = cross(normal, tangent) · w). Unchanged corners keep their doubles;
  an added set is written whole, a dropped one removed. They are written only
  where the importer reads them from the file (`Normals: Import`, and for
  tangents also `Tangents: Import`): under Calculate Unity recomputes them on
  every import, so a changed set is reported in the log and left out. A hard
  edge inside one polygon (two normals for one corner) is refused like a UV
  seam. Smoothing groups are left as they are.
* "Save channels only" writes UV sets and vertex colours only.
* The hub's Overwrite / Export New FBX (`All`) takes this path, together with
  the structure edit below, in one load and one save of the document.

### Structure edit in the FBX document (generated LODs, collision, edited faces)

The hub's `All` save adds or replaces geometry in the same document
(`Editor/Assets/FbxStructureWrite.cs`, SDK half `FbxStructureEdit.cs`):

* A generated LOD becomes a node next to the node it was generated from
  (same parent), with its local TRS, rotation order, pivots, pre/post
  rotation, geometric transform, inherit type and materials; the copy must
  evaluate to the source's global transform or the save is refused. Unity
  then groups it by name (`base_LOD0` … `base_LODn`).
* Sidecar collision becomes `{key}_COL` (one simplified mesh) or a `{key}_COL`
  container of `{key}_COL_Hull{i}` nodes next to its source, with positions /
  triangles / normals only: no UV, no vertex colour, no material (the
  TS_UnityExport_SDK collider rule). Collision the file already holds with the
  same geometry and no UVs or colours is left alone; one with UVs or colours
  is rewritten clean.
* A mesh whose faces or points changed gets a new FBX mesh on the same
  node(s); node, transform and materials stay. Quads (Keep Quads imports) are
  written as quads, and each submesh keeps its material slot. Instances edited
  differently (any written attribute: positions, faces, normals, tangents,
  colours, UVs) are refused.
* Collision is placed next to the mesh named by its sidecar key, or the one
  LOD0 / unsuffixed mesh with that group key. Collision the file already holds
  is compared as triangle loops on the same points, not only point sets.
* Unity-space geometry goes back into control-point space through the affine
  map fitted to the source mesh's tagged import (`FbxSpaceFit`): whatever
  axis conversion, unit scale, mirroring or baked pivot the import applied is
  in that map. A position the source import had takes its exact control
  point; a flat source takes its mirror from the import's winding. The
  triangle winding is reversed when the import reversed it, so a reimport
  gives the Unity triangles back. A flat source tells the map only within its
  plane: geometry leaving that plane is refused unless the plane maps without
  stretch and the source's geometric scaling is uniform. New meshes carry
  normals, the source's UV sets under the source's set names (read from the
  file by `FbxUvSetNames`, since a texture finds its set by name; `UVChannel_N`
  when they cannot be read), plus the UV sets MeshLab added within the save's
  intent (a repacked UV1 the file lacked; never a UV1 the import regenerates:
  with 'Generate Lightmap UVs' on and no UV1 bake, a source's set there is
  kept in the count but is no UV1 write and leaves the setting on),
  set i on layer i, and layer-0 colours when the source has them or the save
  writes colours, all per corner, indexed and shared only within a control
  point. Faces without area (repeated or collinear corners) are left out. Normals, colours, the tangent frame and the one material
  element sit on layer 0 (TS_UnityExport_SDK I4); polygons are begun without
  the legacy material bookkeeping; a node given materials gets texture
  shading.
* Not written, by limitation: a smoothing-group element (the C# FBX SDK
  wrapper has no `FbxLayerElementSmoothing`), so a new or replaced mesh carries
  its shading as explicit normals only; a DCC rebuilds smoothing from them
  (TS_UnityExport_SDK `SmoothingFromNormals`). Tangents / binormals are
  written only when the importer imports tangents (otherwise Unity computes
  MikkTSpace, as with the TS exporter's `stripTangentsBinormals`).
* Nothing is renamed, moved or normalised; a node is removed only when a new
  one takes its name next to the same source (same parent; a same-named node
  in another branch is left alone; a replaced node with children is refused). Hierarchy
  normalisation stays the explicit Prefab Builder action (the LOD-rebuild path).
* A renderer whose materials differ from the ones its mesh's node gives it
  (Cleanup's material fixes; compared with the prefab source renderer, or for a
  renderer that is no prefab instance with the model's renderer of the same
  mesh) changes only the node slots of those submeshes: each takes an FBX
  material named after the Unity material (the scene's own of that name, or a
  new one; `_1`, `_2`… when the name already maps to another material), and the
  importer gets a remap from that name to the asset. Other slots and other nodes
  sharing the old material keep it. A generated LOD whose renderer's materials
  differ from its source renderer's gets them in its own slots the same way.
  With material import off nothing is written (Unity does not use the file's
  materials); an adopted LOD renderer keeps the generated one's materials.
* Refused, nothing written, then a dialog offers the rebuild or "Save channels
  only": a changed material that is not an asset or has no slot in the FBX, a LOD source node without an `_LOD<N>` suffix, a source that is the
  file's only top-level node (Unity imports it as the model root) unless the
  importer preserves the hierarchy, a skinned or blend-shaped source, a source
  whose transform is animated (a sibling copies the values, not the curves),
  an import that is not an affine image of its control points, a generated LOD
  whose source mesh cannot be told or whose name two branches generate, a
  sibling of a mesh instanced by
  several nodes (which instance it belongs with cannot be told), a mesh
  written whole that lacks a UV set before one it has (its sets would be
  renumbered) or lacks the UV1 a bake writes for the whole model, a new node
  whose copied transform does not evaluate to its source's placement,
  a `_COL` mesh of the file carrying UVs or colours that no sidecar collision
  replaces (Cleanup sends it to the overwrite; the rebuild strips it).
* After an overwrite, the scene LODGroup's slots take the imported renderers
  of the written LODs (transitions kept) and those generated objects go; one
  whose name matches no imported renderer or several keeps its slot. A save of
  several FBX files frees the working copies, adopts the LODs and reloads the
  scene once, after the last file; a rebuild picked for a refused file
  finishes after that.

### `FbxExportIntent` flags

| Flag | When to set |
|---|---|
| `UV0` / `UV1` / `UV2` / `UV3` … `UV7` | Tool overwrote the corresponding `Mesh.uv*` channel |
| `VertexColors` | Tool wrote `Mesh.colors` / `colors32` |
| `Normals` | Tool wrote `Mesh.normals` |
| `Tangents` | Tool wrote `Mesh.tangents` |
| `Hierarchy` | Tool added/removed/renamed nodes (LOD gen, collision injection, root pivot reset) |
| `Materials` | Tool reassigned `Renderer.sharedMaterials` |
| `Collision` | Tool changed `_COL` children (V-HACD, sidecar inject) |
| `LodGroup` | Tool added/removed LOD entries on the source LODGroup |
| `All` | LOD-rebuild scenario — every aspect changed |
| `None` | No-op (logged + early-return) |

### Per-tool intent recipe

| Tool | Intent it produces |
|---|---|
| `UvPackHierarchyTool` (atlas pack) | `UV2` |
| `LightmapTransferTool` UV2 transfer | `UV2` |
| `VertexColorBakingTool` (AO bake to vcolor) | `VertexColors` |
| `VertexColorBakingTool` (AO bake to UV channel N) | `VertexColors \| UV<N>` |
| `Uv0Analyzer` / UV0 Optimize | `UV0` |
| `LodGenerationTool` (new LODs) | `Hierarchy \| LodGroup \| AnyUv \| VertexColors \| Normals \| Tangents \| Materials` |
| `CollisionMeshTool` (V-HACD into sidecar) | `Collision` |
| `LightmapTransferTool` "Rebuild LOD chain" | `All` |

### Hard rules for every FBX-write path

1. **No new public `Export*` methods.** Adding a parallel
   write path duplicates the importer-prep / .meta-backup /
   postprocessor coordination. Add an intent + delegate to
   the existing core.
2. **Write to `*.fbx.tmp` first**, then `File.Replace` for
   atomicity. The core handles this — never call
   `ModelExporter.ExportObjects` directly on the source path.
3. **Pre-export preflight runs inside the core**, not at
   call sites. Every caller benefits automatically.
4. **`.meta` survives the round-trip** via temp backup and
   conditional restore — the core handles this too.
5. **Postprocessor coordination** (`Uv2AssetPostprocessor.bypassPaths`
   on importer-prep reimport, `fbxOverwritePaths` on the
   write itself) is the core's responsibility. Call sites
   never touch these sets directly.

### Quad preservation (`keepQuads`)

A re-save must not triangulate a quad mesh. Unity meshes carry topology in
their index buffer, and the FBX exporter writes whatever topology the
serialized mesh has — so the clone that reaches `ModelExporter` must be
imported with `keepQuads = true`:

* **Channel re-save (UV / vertex colours)** — not affected: the FBX
  document's polygons are never rewritten, so quads and n-gons stay as
  authored whatever `keepQuads` is set to, and `keepQuads` is not touched
  (a save that writes UV1 does switch `generateSecondaryUV` off, see above).
* **Isolated core (normals / tangents / hierarchy / materials intents)** —
  Phase 1 enables `keepQuads` on the source importer
  (alongside `isReadable`) before the clone is loaded. `keepQuads` only
  reshapes the index buffer (4 indices per quad instead of two triangles);
  vertex order and count are untouched, so the snapshot/clone
  vertex-count contract holds. The value persists after export (like
  `generateSecondaryUV = off`) — restoring `false` would triangulate the
  just-written quad FBX on the next import.
* **Variant exports** (`ExportVertexColorsToFbxAs`) — the source importer
  must end the export unchanged, so `keepQuads` is toggled on only for the
  clone reimport and restored by `FbxExport.ImporterRestoreScope` at method exit —
  success, every early return and failure alike, a throwing Phase 1 reimport
  included: the scope is created before Phase 1 and told about each change
  before the reimport that applies it (the same scope puts `isReadable` back
  for source re-saves). The new variant file's own importer
  gets `keepQuads` pinned in Phase 4 so its project view matches the file.
* **Wide LOD-rebuild path** — already locks `keepQuads` via
  `PrepareImportSettings(lockForFbxOverwrite: true)` before export. Its
  export meshes take topology from the tool's computed result meshes, so
  the first export of a freshly loaded (triangulated) import still writes
  triangles; once the lock has persisted and the model reloaded, every
  later export keeps quads.
* **Known limitation** — `keepQuads` covers quads only. N-gons
  (>4 vertices per face) still import triangulated, and generated LODs
  (meshoptimizer output) are triangle meshes by construction.

### New-geometry export (Remesh & Bake) — the one out-of-core write

`RemeshBakeTool.Save()` exports a **brand-new mesh** (voxel remesh result)
into a **uniquely created folder**, gated by the same
`LIGHTMAP_UV_TOOL_FBX_EXPORTER` define. It calls
`ModelExporter.ExportObjects` directly because the isolated core is a
*re-save* pipeline: its snapshots key on mesh name and require the same
vertex count, so it structurally cannot carry re-topologized geometry,
and there is no source FBX whose untouched channels must survive.
Safety comes from the fresh-path guarantees instead: the target file
never pre-exists (a new unique folder), the output is verified
non-empty, and any failure rolls the whole generated folder back.
The FBX importer of the result is pinned to `materialImportMode = None`
so the importer does not duplicate the curated material that ships
next to the FBX. Do not use this carve-out for anything that mutates
an existing FBX — that path must go through the core.

### What "rework, not parallel" means in code review

Reject PRs that:

* Add a new method named `Export*Fbx*` on any tool other
  than `LightmapTransferTool`. Tools push their changes
  into mesh entries / sidecars and call the existing core
  with the appropriate intent.
* Call `UnityEditor.Formats.Fbx.Exporter.ModelExporter.ExportObjects`
  outside the core.
* Bypass intent (e.g. always passing `FbxExportIntent.All`
  when the operation only changed one channel) — this
  defeats the safe-resave contract and risks collateral
  mutation of unrelated mesh data.
* Add `if (myToolDidThing) NormalizeExportHierarchy(...)`
  branches at call sites — that pass belongs in the core,
  gated by `intent.HasFlag(Hierarchy)`.
