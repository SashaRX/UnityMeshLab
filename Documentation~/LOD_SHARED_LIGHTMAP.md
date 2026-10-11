# Shared LOD0 lightmap on the 64px fire shield

The fixture is `Level_Plan / TransformerStation_Building / FireShield`, supplied
by the user. The nested FBX is the same `FireShield.fbx` used by the preceding
eight-model evaluations. Its imported UV2 is authored (`generateSecondaryUV: 0`).
Original scene, prefab, FBX and atlas files are read only; texture copies are
imported in the isolated test project.

## Actual binding

Bakery's scene storage associates renderer IDs `1352331050`, `1352331051` and
`1352331053` with the same atlas **22** and scale/offset:
`(0.025706554, 0.025744658, 0.23560758, 0.7140595)`.
The renderer IDs follow the nested FireShield prefab into the building prefab;
they are not a guessed assignment from the standalone FBX.

The atlas is `LMGroup_Level_Plan_Smallbuildings_final.hdr`, with
`LMGroup_Level_Plan_Smallbuildings_dir.tga`. The copied color texture imports as
**1024×1024 BC6H**, with bilinear filtering and no mipmaps. The report preserves
scene/color/direction SHA256 hashes, binding, source hierarchy and root rotation.
Dominant-direction evaluation uses the mesh-to-root transform and the actual
FireShield rotation in the building. The building and its scene parent have
identity rotations.

The GPU shader samples this actual full-HDR bake through UV2 and the recorded ST,
with the combined dominant-direction equation in a second pass. It evaluates
baked irradiance. Original PBR materials, normal maps, reflection probes and
shadowmask direct lighting are outside this diagnostic.

## Transfer and acceptance

LOD2 remains **204 triangles from 1850** (11.03%, or a 9.07× reduction). The
normal stage's improved silhouette is retained. Geometry, winding, material
slots, normals, tangents, vertex RGBA and all other UV channels remain the same
at each triangle corner.

The transfer finds source UV2 charts through shared geometric/UV edges within
each material slot. Non-manifold edges do not join charts. A proposed triangle
projects its corners onto a nearby, compatible source chart. Render vertices
split when chart assignments or transferred UV2 differ; triangles are not added.

Candidates are checked against the **actual baked color and dominant-direction
fields**, averaging covered samples per pixel at a 64px extent. Both sides of
the six source directions are included. Each changed face must retain or improve
each field in each covered view. Only faces covering at least **16 distinct
pixels in one view** are eligible. Small faces retain the preceding UV2.
The geometric UV RMS is a diagnostic for the baked-field path, rather than its
acceptance objective.

On this mesh, **17/204 faces** change UV2 and render vertices rise **259→288**.
UV2 RMS changes **0.279411→0.267225**. The vertex increase is part of the cost,
even though the requested triangle budget and silhouette are unchanged.

The generic geometric-chart experiment lowered UV-coordinate error but worsened
actual baked lighting. Fits optimized per subpixel or on smaller faces also
worsened the source-framed 64px comparison. They are retained as local experiment
evidence and are not the Generate policy.

Independent GPU captures use the same source framing and box-filtered 64px object
extent as the preceding reports. The conservative variant improves all four
covered-overlap linear RGB comparisons:

| View | Baked field | Previous LOD2 RMS | Transferred LOD2 RMS |
|---|---|---:|---:|
| Front | Color | 0.124047 | 0.122747 |
| Front | Directional | 0.090564 | 0.089959 |
| Oblique | Color | 0.118005 | 0.116770 |
| Oblique | Directional | 0.087427 | 0.086852 |

This is a modest improvement, about **0.66–1.05%**, not a solved lighting-fidelity
problem. The remaining coarse geometry and UV chart boundaries still need work.
The figures show the actual residual error rather than hiding it with a headlight.

## Generate integration and limits

LOD Gen and Prefab Builder share the same generation path. For the optional far
screen-budget profile with attribute correction, Generate captures the source
renderer's assigned `LightmapSettings` binding and tests the UV2 transfer after
geometry/normal selection. The profile is limited to a 64px diagnostic footprint
and at most 512 triangles. Near LODs keep their existing policy.

The currently supported binding is full HDR (BC6H/RGBAHalf/RGBAFloat), bilinear,
unmipped, with Clamp/Repeat addressing and a float readback-capable GPU. Missing
bindings, RGBM/other encodings, mipmapped atlases, unsupported sampling and
deforming meshes retain their existing UV2. Texture readback restores the active
render target and destroys temporary textures. A cancelled/rejected mesh is
destroyed; source textures and meshes are never modified. Existing renderer
copying retains the lightmap index and ST on generated renderers.

The actual-atlas integration test temporarily supplies the recorded binding and
generates through `LodPipelineOps.Generate`. Its LOD2 must match the independently
transferred mesh, retain 204 triangles and report accepted shared-lightmap UV2.
Lightmap settings and temporary objects are restored in `finally`.

## Artifacts

The final selected Unity EditMode run passes **220/220 tests**, without skips.
It includes the actual-atlas Generate integration, all three UV2 transfer tests,
normal/screen/budget/lifecycle regressions and the existing eight-model GPU replay.
The preceding normal-stage eight-model generation supplies fresh source/native/far
controls. The final binding evaluation regenerates the fire shield and verifies
its source/native/far raw controls against that generation.

- [Measured data and provenance](LOD_SHARED_LIGHTMAP_RESULTS.json).
- [Actual model comparisons and error maps](LOD_SHARED_LIGHTMAP_RESULTS_MODELS.png).
- [Normal-stage measurements on all eight models](LOD_FAR_NORMALS.md).

Local raw captures, integration/unit results and rejected trials are under
`_results~/lod-far-normals-20261010/shared-bake/`. Copied project assets remain
outside the package. Rebuild the portable lightmap report with:

```powershell
python -B Tools~/render_lod_shared_lightmap.py `
  _results~/lod-far-normals-20261010/shared-bake `
  --baseline _results~/lod-far-normals-20261010/final `
  --output Documentation~/LOD_SHARED_LIGHTMAP_RESULTS
```
