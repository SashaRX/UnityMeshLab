# Far LOD fire-shield normals and lightmap priorities

This follows [the silhouette and RGBA stage](LOD_FAR_REFINEMENT.md), commit
`408d985`. The same project FBX copies, LOD0/1/2 ratios and 64-pixel diagnostic
footprint are used. This stage corrects attributes after the existing geometry
selection. It does not change positions, indices, triangle counts, material
slots, UV channels or vertex colors. Native code and distributed binaries are
unchanged.

## Bounded visible normal correction

The fire shield's far faces cross several source smoothing regions: the
preceding stage records 150 regions, 83 mixed target faces and 125 regions
without a mapped target face. Strict surface-region fitting leaves many
retained normals unchanged, even after vertex-position silhouette correction.

The final far pass tries a visible normal fit only for static meshes with
authored source/target normals, at most 512 target triangles, at most 128
diagnostic object pixels and worst-view normal RMS of at least 75 degrees.
This confines the extra work to visibly poor coarse fields. It runs only when
far screen budgets and attribute correction are enabled. LOD1 and earlier
geometry/candidate decisions retain their existing policies.

Depth-tested covered subpixel samples pair LOD0 and the selected LOD2 in the
same six frames. Matching material slots and compatible geometric face
orientation are required. Hidden samples and opposite faces cannot lend
normals. Sampled normals are transformed back into local space before fitting.
Only source-verified continuous render duplicates share a fitting variable;
hard splits remain independent and multi-material vertices stay pinned.

Sparse barycentric least squares tries priors 0.1, 0.02 and 0.002, plus a
mass-average alternative; each tries blends 1, 0.5 and 0.25. The maximum is
12 full verification trials. Unlike the near surface fit, the old retained
normal's hemisphere cannot block correction of a stale far field. The outputs
are normalized and tangent vectors are re-orthogonalized with their handedness
retained.

Acceptance requires improvement of both worst-view normal RMS and diagnostic
headlight error, with neither metric increasing in any of the six views.
Silhouette, local detail and each visible RGBA channel must not worsen. Sampled
surface normal RMS and authored maximum must also not increase. Rejected
meshes are destroyed and cancellation does not leak the owned clone.

The headlight diagnostic matches the preview multiplier
`0.25 + 0.75 * abs(dot(normal, viewDirection))`, averaged over covered subpixel
samples. The CPU views are orthographic; separate GPU front/oblique renders
verify the result independently. Source-region RMS/max remain measured final
diagnostics for the cross-region far fit, rather than a strict acceptance gate.
The earlier surface-fit stage retains its region gate; near-level tests still
require it. Region diagnostics describe the actual final normal field.

## Lightmaps are the primary lighting target

The user clarified that these assets will be lit with lightmaps. Normal and
headlight diagnostics are secondary; their improvement is not evidence that
actual baked lighting improved. UV2, chart boundaries, atlas assignment and
screen-space lightmap sampling must drive the next lighting evaluation.

The project has baked color/direction assets under `Scenes/Level_Plan`, and
`Level Lighting Settings.lighting` enables baked lightmaps with
`m_LightmapsBakeMode: 1`. Directional lightmaps store both lighting color and
dominant direction ([Unity 6.2 documentation](https://docs.unity3d.com/6000.2/Documentation/Manual/LightmappingDirectional.html)).
Thus normal work remains useful as a secondary diagnostic; it should not
justify more triangles at this footprint.

The current fixture is the same largest eligible mesh from the original FBX,
not a separate procedural test mesh. The supplied `Level_Plan /
TransformerStation_Building / FireShield` instance establishes the actual
Bakery atlas/ST association; see [the shared-lightmap evaluation](LOD_SHARED_LIGHTMAP.md).
The normal pass retains UV2 exactly, but this does not prove
that the preceding simplification or silhouette correction preserved the
source's lightmap appearance. Its source UV2 RMS is a UV-coordinate diagnostic,
not a measured baked-light RGB error.

The user confirmed **one shared LOD0 lightmap, with UV2 transferred to LOD2**.
The shared-atlas follow-up now uses that exact bake, source renderer association,
scale/offset and directional texture in source-framed 64-pixel views. Atlas
coordinates are transferred without independent repacking. Next coverage should
include prepared working LOD0 after other repack/transfer operations, mip filtering,
stronger chart/seam leakage checks and original-material/scene rendering. Preserve
silhouette, paint and approximately 1/9 budgets. Visible baked-light error remains
the primary lighting objective at this footprint.

## Verification and artifacts

The fresh eight-model run passes 216 tests, with one opt-in existing-readback test
completed in a separate passed run after supplying its required 22-capture input.
Thus all 217 selected normal-stage tests pass. The later shared-atlas integration
and UV2 tests are recorded separately in the linked lightmap report.

Fire shield remains 204 triangles; mean/max silhouette errors stay exactly
14.2062%/16.5400%. Worst-view normal RMS improves 103.6187°→81.1379° and headlight
multiplier RMS 0.312093→0.144236. Independent GPU 64px headlight RGB RMS improves
0.147297→0.111585 front and 0.118240→0.098003 oblique. These remain secondary
diagnostics. Fourteen source smoothing-region diagnostics increase after the
final far fit; near-region enforcement remains strict.

All eight triangle budgets, far color/coverage readbacks and source/native/near
controls are unchanged; seven other far shaded controls are identical. The
subsequent lightmap pass preserves these geometry/shading controls and changes
only UV2/seam vertex ownership on the actual bound fire-shield test.

- [Portable data](LOD_FAR_NORMALS_RESULTS.json).
- [Error graph](LOD_FAR_NORMALS_RESULTS_ERRORS.png).
- [Actual model comparisons](LOD_FAR_NORMALS_RESULTS_MODELS.png).

Local raw captures and NUnit results: `_results~/lod-far-normals-20261010/final/`.

```powershell
python -B Tools~/render_lod_far_normals.py `
  _results~/lod-far-normals-20261010/final `
  --previous _results~/lod-far-fidelity-20261010/final `
  --completion _results~/lod-far-normals-20261010/shared-bake/readback.xml `
  --output Documentation~/LOD_FAR_NORMALS_RESULTS
```
