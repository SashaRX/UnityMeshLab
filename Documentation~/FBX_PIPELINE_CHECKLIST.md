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
