# LOD iteration handoff — 2026-10-11

Iteration [#229](https://github.com/SashaRX/UnityMeshLab/pull/229) was merged into main and released as 1.1.37. The complete LOD quality roadmap is not finished. The intended hierarchy remains LOD0, LOD1 and LOD2, with independent source reduction to approximately 1/3 and 1/9 of LOD0.

The subsequent [feature-chain experiment](LOD_FEATURE_CHAINS.md) adds an optional managed prepass with original-polyline/shading checks, contact/component isolation and quality-ranked native retries. The [native-constraint follow-up](LOD_NATIVE_CONSTRAINTS.md) now allows neighboring patches to retriangulate while preserving validated creases, junctions, material borders and ambiguous faces. Both options remain disabled by default. The evidence below includes unmerged far-budget follow-ups; the preceding merged iteration is retained as historical context.

Native-constraint experiment verification: **183 selected EditMode tests passed**, with all eight meshes and 176 variant/view GPU captures freshly evaluated at a physical 384×384 target. Native builds and CTests passed on Windows/Linux/macOS through CI. Native constraints reach 6/16 source budgets; only 2/8 LOD2 budgets pass, and Bench B LOD2 selects a reported strict-belt fallback. At matched counts, Wrench/First Aid Kit/Fire Shield LOD2 normal RMS improves 67.8%/48.6%/54.7%; tent LOD2 RGBA RMS improves 30.5% at exactly 280 triangles. Other color/normal/silhouette regressions and slow native retries keep this experimental. Details, portable graphs/data and the next checks are in the native experiment. The 177-test managed-prepass comparison remains its historical control.

## Implemented

### Current decision: quality experiments closed (2026-10-11)

[The final component-allocation attempt](LOD_PART_ALLOCATION_CLOSURE.md) used
78/79 triangles on SmallBench but worsened mean silhouette error, thin-detail
loss and UV correspondence. It was rejected. The prototype and its tests are
archived in a reproducible patch; they are removed from the shipping code so
generation does not pay for the rejected alternative. **Retain the generator
from `7739c46`. Further vertex-color/silhouette parameter experiments on these
fixtures are closed and must not restart without a new request.**

Fresh actual Generate and fourteen-view 64px GPU checks retain SmallBench at
77 triangles / 125 vertices, University Pot at 74 / 71 and the tent at 62 / 154.
Their selected mesh snapshots are byte-identical and coverage/RGBA results are
unchanged. The final attempt passed 237 selected EditMode tests with zero
failures; five unrelated opt-in experiments were ignored and are explicitly
listed in its report. Both FBX compile configurations passed. Missing SmallBench
frame details remain a known limit; its authored 144-triangle LOD2 is better.
Protected LOD1 budgets, generation performance and original-material/shared-
lightmap integration remain unfinished. This is an experiment closure, not
production acceptance.

### Retained: bounded thin-frame silhouette correction (2026-10-11)

[The silhouette iteration](LOD_SILHOUETTE.md) freezes the previous vertex-color
algorithms and improves the real SmallBench frame at **77/708 triangles and
125 vertices**. Fourteen-view GPU mean/worst coverage error falls
**10.435%/23.881% → 8.713%/18.064%**. Colors, UVs, tangents and triangle arrays
remain exact; worst RGBA/alpha and source UV correspondence improve. University
Pot and the tent retain their previous meshes and GPU results.

The stage now catches a bad individual view hidden by a low mean, verifies each
group before committing, checks new contacts and vertex sweeps, and uses both
64px and 128px source guides. Post-position attribute fitting also verifies
reverse views. **264 selected EditMode tests and both FBX compile configurations
pass.** The linked report includes actual masks, authored comparisons, per-view
GPU graphs and numeric evidence. SmallBench generation increased from 41s to
104s; source-data caching/local evaluation and remaining thin-part allocation
are the next work. Original material/shared-lightmap acceptance and protected
LOD1 budget fallbacks remain unfinished.

### New model validation: university and apartment benches

[The vertex-color generalization check](LOD_VERTEX_COLOR_BENCHES.md) runs the
current generator on the actual university bench Pot and an additional
SmallBench with varying RGB/alpha and two material slots. LOD2 reaches
**74/673 and 77/708 triangles**, with 71/125 render vertices. Fifty-seven selected
EditMode tests pass and both FBX compile configurations pass, with fresh source,
author and generated GPU controls across fourteen color views per model.
This is test/reporting work; production algorithms are unchanged.

The isolated-pair diagonal correction makes no changes on these shared-corner
meshes. University mean GPU RGBA and coverage improve against its 114-triangle
author LOD2, but its weakest top-view color is worse. SmallBench loses thin
frame parts and has large RGB/alpha drift against its 144-triangle author LOD2
(worst RGBA 0.02870 -> 0.15575; worst alpha 0.03021 -> 0.18464).
Both CPU paint guides fail, and both protected LOD1 budgets are missed. Shared
vertex/material support, weak-view GPU validation and SmallBench frame retention
now take priority over additional tent-only tuning. Source hashes, limitations,
actual model images and graphs are retained in the linked check.

### Earlier: far paint diagonals and shared UV2 (2026-10-10)

The far Screen Budget path now tries local diagonal changes on isolated,
nearly coplanar pairs near lost source paint boundaries. Existing render corners
are reassigned; no triangles, vertices or geometric positions are added. UV and
authored shading seams, material splits and deformers refuse this operation.
The small-mesh limit is 128 triangles. Each accepted candidate must improve
the six-view paint guide while retaining silhouette/detail guides, source
distance RMS/maximum and the twelve-view RGBA allowance of 1/255 per-channel RMS.
New contacts against other sheets are refused relative to the existing mesh;
this does not certify an intersection-free input.

Two-dimensional UV2 is fitted from visible source correspondences and projected
into the original source islands. Ambiguous island seeds remain pinned; missing
or mismatched UV2 fields cannot silently pass an evaluated transfer. Full-surface
UV2 RMS must retain or improve, and each paired-view UV2 RMS increase is capped
at **0.00002 normalized UV**. Higher-dimensional UV fields retain their values.
CPU paint samples now use one consistent owner on an exact triangle edge,
avoiding color differences caused solely by triangle ordering.

Fresh actual Generate tests keep the same rainbow tent at **62/560 triangles
(9.03x), 154 render vertices and 33 geometric positions**. One accepted diagonal
change improves mean lost paint pairs **44.09% -> 39.25%** (11% relative); sampled
UV2 RMS improves **0.015978 -> 0.014188** (11.2%). Eleven of twelve paired UV2
views improve; the remaining increase is 0.00001949. These are coordinate
correspondence measurements, **not an actual baked tent atlas lighting test**.
Silhouette coverage is unchanged. GPU vertex-RGBA RMS at a 64px maximum source
extent improves front **0.008800 -> 0.008714**, but worsens oblique
**0.013156 -> 0.013342**. The dedicated paint-edge view changes
**0.030410 -> 0.030564**; the opposite paint angle changes
**0.020037 -> 0.020267**. Original project materials and the complete stall
assembly are still outside this diagnostic.

The red guide recovers three eligible sample pairs. **Blue/violet paint regions
remain incomplete, worst-region loss stays 100%, and paint acceptance is false.**
An offline real-mesh prototype moved an interior roof point to the missing blue
strip boundary while retaining the budget. It improved one color view but raised
another silhouette error from 16.3% to 24.6%; it was rejected and is not part of
the generator. Further progress needs coordinated source-based patch allocation,
not a single large vertex move or more color fitting.

The same colored bench retains **1032/9334 triangles and 2251 vertices**. This
small-mesh stage bypasses it; all thirty source/native/near/far GPU fields across
the two recorded views are byte-identical to the preceding corner-sharing run.
**249 selected EditMode tests pass, zero failures/skips**, on Unity 6000.2.6f2,
including fresh Generate captures for both models and the new diagonal, UV-island,
packed-color, edge-ownership, refusal and cancellation checks. Both FBX compile
configurations and static dependency/identifier checks pass. No native ABI or
binary changed. One tent two-level far generation sample takes **21.84s vs
18.63s** before; this is not a repeated performance benchmark.

Portable evidence: [measured results](LOD_PAINT_GEOMETRY_RESULTS.json),
[actual GPU models](LOD_PAINT_GEOMETRY_MODELS.png),
[error chart](LOD_PAINT_GEOMETRY_ERRORS.png). Raw selected tests and captures:
`_results~/lod-paint-geometry-20261010/final/`. Reproduce the portable report with
`python -B Tools~/render_lod_polygon_paint.py _results~/lod-paint-geometry-20261010/final _results~/lod-tent-paint-20261010/final-complete Documentation~/LOD_PAINT_GEOMETRY --retriangulated --bench-baseline _results~/lod-corner-sharing-20261010/final-exact`.

### Preceding: polygon paint

The **polygon paint follow-up** replaces stretched gradients with constant source
colors on selected faces and their already-shared render corners. It searches
the lost paint pairs, including small regions counted in the mean, and their
one-pixel correspondence neighbors, rather than
flattening every face. Positions, triangle order, winding, normals, tangents and
all UV fields remain intact; the current 154-vertex buffer is retained. Static,
single-material far meshes with at most 128 triangles are eligible. Dense meshes,
material splits and deformers bypass this stage. No public setting or native
binary/ABI changed.

Six primary paint views must retain or improve each boundary guide and the worst
RGBA RMS. Twelve views, including reverse directions, cap each channel/view RMS
increase at **1/255 linear RGBA**. This is a small far-LOD color allowance, not an
exact-color guarantee or a maximum per-pixel error bound. The initial unrestricted
flat-color result worsened front GPU RMS by 0.00139 and was rejected. Requiring
zero error increase in every reverse view rejected all tent replacements; the
bounded policy follows the intended LOD2 quality tradeoff. Approximate vertex
sharing remains disabled.

At a 64px maximum source extent, the same actual tent retains **62/560 triangles
(9.03x) and 154 vertices**. Mean lost paint pairs improve **50.09% -> 44.09%**
(12% relative), and worst primary-view RGBA RMS improves 0.032515 -> 0.032258.
GPU front RMS changes 0.008613 -> 0.008800; oblique RMS improves 0.013255 ->
0.013156. The dedicated paint-edge camera improves 0.031000 -> 0.030410;
the opposite paint angle changes 0.019991 -> 0.020037. Geometry/coverage and
diagnostic headlight fields are unchanged. These are diagnostic vertex-color
passes; original materials and the complete stall assembly remain unverified.

The cyan paint region recovers its two previously lost eligible pairs. **The red,
violet and blue regions are not fully repaired; worst-region loss is still 100%**,
so paint acceptance still fails. This is a partial improvement, not a production
quality certification. Moving corners in the exploratory replay helped some
boundaries but changed normal/coverage errors; those geometry experiments were
not enabled. The next step needs source-derived triangle placement along paint
regions, particularly the blue valance, rather than more attribute fitting.

**239 selected EditMode tests passed, zero failures/skips**, with a fresh actual
tent Generate capture on Unity 6000.2.6f2. Cases include packed/float RGBA, retained
geometry/UV/shading/storage, reverse-view drift refusal, dense/material/deformer
bypasses, cancellation after allocation without leaking the owned clone, and the
recorded actual-model paint improvement. Both FBX compile configurations and
static dependency/identifier checks pass.

The active-face search reduces the trial count from 231 to 156 (26 of 38 connected
color groups), retaining the preceding unrestricted search's exact final mesh
buffers and fields. Omitting small regions initially lost one improvement and
was caught by the real-model regression check; they must be included because
mean boundary loss counts them. One local two-level generation timing sample is
18.63s versus the preceding 14.74s; extra cost still needs profiling, and this is
not a repeated performance benchmark.

Portable evidence: [measured fields](LOD_POLYGON_PAINT_RESULTS.json),
[actual GPU model comparison](LOD_POLYGON_PAINT_MODELS.png) and
[error chart](LOD_POLYGON_PAINT_ERRORS.png). Raw current-code captures and selected
tests are under `_results~/lod-tent-paint-20261010/final-complete/`.
Reproduce the portable figures with
`python -B Tools~/render_lod_polygon_paint.py <current-capture-dir> <baseline-snapshot-dir> Documentation~/LOD_POLYGON_PAINT`.
The source FBX SHA256 remains
`a4aafe5dc9b4bc31200f31d1a0549a93b729aa7d657bcb7501acdd310e616839`.
The colored bench and the other project models were not freshly recaptured in
this follow-up. The bench's 1032-triangle LOD2 bypasses the new 128-triangle limit.

The **render-corner sharing follow-up** compacts the accepted far color fit only
where RGBA, positions, normals, tangents and all UV channels match exactly.
Unambiguous reversed-edge adjacency limits sharing to neighboring corners;
material slots, hard-field seams, overlapping shells and corner contacts remain
separate. Deforming meshes bypass sharing and cancellation releases candidates.
Triangle order, winding and the selected approximately 1/9 triangle count remain
unchanged. No public setting, native ABI or plugin binary changed.

An approximate 1/255 color-diameter trial reduced the bench from 3096 to 2146
vertices and improved worst CPU RGBA RMS from 0.02605082 to 0.02604022, but actual
64px GPU RMS rose from 0.01356405 to 0.01356752 in front and from 0.01420742 to
0.01421247 obliquely. It was rejected for generation: exact RGBA sharing is the
only enabled policy. The low-level bounded-sharing primitive remains exercised
by unit tests, without an enabled approximate generation path.

**232 selected EditMode tests passed, zero failures/skips**, on Unity 6000.2.6f2,
with final-code Generate captures of the same two project models at a 64px
maximum source extent. Both FBX compile configurations and static dependency/
identifier checks pass. The six-view CPU fields and boundary losses are unchanged.
Front/oblique GPU RGBA readbacks match the preceding stage byte-for-byte for both
models; the tent's opposite-side readback also matches its preceding final-code
capture. Source/native/LOD1 color, alpha, coverage, wire and headlight controls,
plus far coverage/wire/headlight readbacks, are unchanged. The other six models
were not freshly recaptured in this stage.

| Mesh | LOD2 triangles / source | Fitted → shared render vertices | Vertex reduction |
| --- | ---: | ---: | ---: |
| Colored bench | 1032 / 9334 (9.04×) | 3096 → 2251 | 27.3% |
| Tent | 62 / 560 (9.03×) | 186 → 154 | 17.2% |

The remaining vertices include real paint/shading/UV splits. Source vertex counts
are 5994/396; the triangle factor is not a measured game-rendering speedup.
Bench worst paint-region loss remains 25%; tent worst-region loss remains 100%,
so this storage optimization does not resolve the tent's remaining paint failure.
Original complete materials, shared filtered/mipped lightmaps and scene transitions
remain outside this diagnostic vertex-field comparison.

Portable evidence: [numeric sharing audit](LOD_CORNER_SHARING_RESULTS.json),
[actual 64px vertex fields](LOD_CORNER_SHARING_RESULTS_MODELS.png),
[unchanged errors and reduced vertex counts](LOD_CORNER_SHARING_RESULTS_ERRORS.png).
Raw captures and `tests.xml`: `_results~/lod-corner-sharing-20261010/final-exact/`.
The rejected approximate trial is retained in `final-adjacency/`; the earlier
stopped pre-adjacency run is not verification evidence.

```powershell
python -B Tools~/render_lod_color_boundaries.py _results~/lod-corner-sharing-20261010/final-exact --previous _results~/lod-color-boundaries-20261010/final --previous-paint-surface _results~/lod-color-boundaries-20261010/paint-surface --corner-sharing --output Documentation~/LOD_CORNER_SHARING_RESULTS
```

The preceding **vertex-color boundary stage** fits independent render corners on the
selected far mesh, without adding triangles or moving geometry. Uniform and
boundary-weighted source-visible fits are checked after Color32 quantization;
Float/HDR and alpha remain supported. Samples cannot borrow from a different
material or opposite-facing face. Mean and per-view boundary loss now accompany
the worst-region guide, and every measured view's boundary loss must not increase.
The pass requires a strict mean-boundary gain, unchanged silhouette/detail/normal
guides and non-increasing worst-view RGBA/channel RMS. Rejected/cancelled expanded
meshes are released; authored positions, normals, tangents and all UV dimensions
are retained. Near LODs, deforming meshes and disabled color validation bypass it.

**226 selected EditMode tests passed, zero failures/skips**, with fresh Generate
captures of the actual tent and colored bench at a 64px maximum source extent.
A further 31-test run recaptured the tent's opposite side with the final code;
its existing six-view measurements and front/oblique raw readbacks exactly match
the complete run. The overlapping test counts are not additive. Both FBX compile
configurations and static identifier/dependency checks pass. Both models' source/
native/LOD1 controls, plus far coverage/wire/headlight readbacks, match the previous
`9c47d01` stage byte-for-byte. Other six models were not freshly recaptured here.

| Mesh | Source → LOD2 triangles | Previous → new worst RGBA RMS | Mean boundary loss before → after the final pass | Render vertices before → after |
| --- | ---: | ---: | ---: | ---: |
| Colored bench | 9334 → 1032 (9.04×) | 0.03437 → 0.02605 (24.2% lower) | 5.28% → 3.93% (25.6% lower) | 1064 → 3096 |
| Tent | 560 → 62 (9.03×) | 0.06452 → 0.03251 (49.6% lower) | 86.11% → 50.09% (41.8% lower) | 48 → 186 |

The mean-boundary columns compare the current selected mesh immediately before
and after the corner pass, not an unavailable six-view mean from the preceding
iteration. Tightened per-view gates also affect the ordinary fit: the bench's
pre-corner RGBA RMS is 0.03849. Independent GPU RMS improves 11–14% for the bench
and 46–53% for the tent across the existing two 64px views. The bench's previously
lost front pair is retained; its worst CPU region reaches the 25% guide. Tent's
worst region still loses 100%, so categorical boundary acceptance remains false.
The existing GPU tent views and the additional opposite side contain no eligible
sharp pairs at this footprint; these are RGBA field checks, not a GPU paint-pass
claim. No categorical labels or original material semantics are recovered.

Render-vertex overhead is material: the source meshes have 5994/396 vertices,
while the accepted far meshes have 3096/186. Selective corner sharing is a next
optimization; the approximately 1/9 claim applies to triangles. Recorded far-
profile generation times are 344.39s/13.68s versus 322.81s/13.07s, including both
levels; these single-run timings do not measure game rendering performance.

Portable evidence: [numeric report](LOD_COLOR_BOUNDARIES_RESULTS.json),
[actual vertex fields](LOD_COLOR_BOUNDARIES_RESULTS_MODELS.png),
[error graphs](LOD_COLOR_BOUNDARIES_RESULTS_ERRORS.png),
[additional opposite-side capture](LOD_COLOR_BOUNDARIES_RESULTS_PAINT_SIDE.png).
Raw captures and `tests.xml`: `_results~/lod-color-boundaries-20261010/final/`,
`paint-surface/` and `corners-reverse/`. An opt-in real-model cost study found that
stronger native RGBA weights distort the tent or exceed its budget; those costs
are not a production profile change. Weighting the unchanged shared vertices
alone did not improve either model. No native ABI/binary or public setting changes.

```powershell
python -B Tools~/render_lod_color_boundaries.py _results~/lod-color-boundaries-20261010/final --previous _results~/lod-components-20261010/final --paint-surface _results~/lod-color-boundaries-20261010/paint-surface --output Documentation~/LOD_COLOR_BOUNDARIES_RESULTS
```

The preceding **far geometry stage** adds one independent LOD0 candidate for static,
non-varying-color meshes with at most 512 target triangles. It retains the
configured normal cost while omitting UV/RGBA costs during that probe, then runs
the existing attribute and shared-lightmap correction. Selection requires better
source quality and no regression in six-view mean/worst silhouette, local detail
or color guides. Authored channels, material slots and Lock Border are retained;
cancellation releases both the new probe and the preceding candidate.

**221 selected EditMode tests passed, zero failures/skips**, on Unity 6000.2.6f2;
all eight actual models were regenerated and recaptured. Both FBX compilation
configurations and the identifier/tool-dependency checks pass. All eight LOD2
budgets remain at approximately 1/9. Fire shield changes 204→200/1850 triangles
(9.25× reduction); six-view mean silhouette mismatch changes 14.21→12.56%, worst
16.54→15.53%. First aid changes 318→316 triangles and mean mismatch 3.14→1.26%;
Bench A changes 413→424 and 7.95→6.40%. The other five LOD2 outputs, including both
varying-color meshes, retain identical raw coverage/color/shaded captures.
Source/native/LOD1 controls are byte-identical throughout the eight-model run.
The added probe has a generation cost: fire-shield far-profile time in this run
is 36.86s versus the preceding 32.97s (both generated levels included). These
single-run timings are diagnostics, not a stable performance benchmark.

With the actual shared Level_Plan Bakery atlas and the same renderer binding,
fire-shield overlapping baked RGB RMS improves **23.39–23.98%** for irradiance
and **40.46–42.22%** with direction. The integrated UV2 transfer is accepted on
26 faces, with 305 render vertices after chart splits. The authored FBX LOD2 has
303 triangles and still preserves silhouette better: front/oblique mask mismatch
8.68/9.92%, versus generated 16.74/18.56%. Thin-strip distortion remains visible.
Diagnostic headlight RMS worsens 0.1442→0.3026; this is a shared-lightmap
improvement, not a general dynamic-lighting improvement. Original PBR materials,
normal maps, complete scene composition and filtered/packed atlases are still
outside this verification.

Portable evidence: [numeric report](LOD_FAR_GEOMETRY_RESULTS.json),
[actual-model comparison](LOD_FAR_GEOMETRY_RESULTS_MODELS.png),
[budget/error graphs](LOD_FAR_GEOMETRY_RESULTS_ERRORS.png).
Fresh raw captures and `tests.xml` are under
`_results~/lod-components-20261010/final/`; the original-FBX inspection control is
under `_results~/lod-original-fire-20261010/`. Reproduce the portable artifacts:

```powershell
python -B Tools~/render_lod_far_geometry.py _results~/lod-components-20261010/final --previous _results~/lod-far-normals-20261010/final --previous-bake _results~/lod-far-normals-20261010/shared-bake --originals _results~/lod-original-fire-20261010 --output Documentation~/LOD_FAR_GEOMETRY_RESULTS
```

Whole-component allocation, position welding and stronger corner/regularization
probes failed the measured non-regression checks and are not shipped. This stage
does not introduce a native plugin/ABI change or a new public setting.

The [far normal stage](LOD_FAR_NORMALS.md) retains all eight approximately 1/9
budgets and silhouette/RGBA controls while improving the fire-shield normal field.
The user confirmed shared LOD0 lightmaps and supplied the actual `Level_Plan /
TransformerStation_Building / FireShield` instance. The [shared-atlas follow-up](LOD_SHARED_LIGHTMAP.md)
now reads its actual Bakery bake and applies conservative UV2 chart transfer to
large far faces when a supported source-renderer binding is assigned. Fire shield
retains 204 triangles; 17 faces change UV2, vertices rise 259→288 and independent
64px baked RGB errors improve by 0.66–1.05% in both views/fields. The lighting
roadmap remains incomplete: mipmapped/packed bindings, stronger chart-boundary
preservation and complete original-material/scene validation remain next.

The [far silhouette and RGBA refinement](LOD_FAR_REFINEMENT.md) passes **215 selected tests** on the same eight models, keeping **8/8 LOD2 budgets near 1/9**. Wrench mean silhouette error drops from 25.91% to 10.41%, fire shield from 23.53% to 14.21%; tent RGBA RMS drops from 0.0807 to 0.0645 at 62 triangles. Bounded position correction keeps indices/counts intact; varying-color candidates receive visible fits before ranking. Fresh source/native/near raw controls are unchanged. The report records trade-offs: fire-shield normals/headlight error worsen, tent/colored-bench silhouette errors increase, paint boundaries still fail, and measured far-profile generation adds 42.23% work. Coarse normal reconstruction and visible component preservation remain next steps.

The [far triangle-budget stage](LOD_FAR_TRIANGLE_BUDGET.md) enables a source-based LOD2 screen-budget profile in LOD Gen. **211 selected tests pass** with fresh native/far comparisons on all eight models; **8/8 LOD2 budgets reach approximately 1/9**. Exact crease/face belts become diagnostics on the far profile; Lock Border and material slots are retained. Six-view silhouette, normal/RGBA fields and local detail/paint guides are ranked at 64 pixels. Colored bench improves several measured errors, while wrench/fire-shield silhouette and other normal/paint regressions still require review. This completes the far-count step, rather than the full fidelity roadmap.

The [far-screen calibration](LOD_FAR_SCREEN_FOOTPRINT.md) evaluates the optional local guide at a 64-pixel maximum object extent for LOD2 and later, matching the intended small on-screen footprint. 206 distinct selected tests pass, including fresh tent/colored-bench generations. Local detail checks now pass at that size; paint guides still fail and selected geometry is unchanged. LOD1 keeps its preceding diagnostic size. Protected-budget limits and the remaining geometry work still apply.

The latest [screen-acceptance stage](LOD_SCREEN_ACCEPTANCE.md) adds local thin-detail and sharp RGBA-boundary diagnostics at three footprints. **196 selected tests pass**, with 528 comparisons on the prior eight-model GPU readbacks and a separate fresh tent generation/capture. The tent proves lower RGBA RMS can still accompany worse sharp-boundary retention. These diagnostic gates do not yet change candidate selection or generation defaults.

- Independent per-level settings and source-based budgets; multiple meshoptimizer candidates ranked by measured geometry, silhouette, normals, UV and RGBA errors.
- Verified normal and vertex-color correction, including connected smoothing regions and correction non-regression checks. Regions represent authored normal continuity, not recovered DCC smoothing-group IDs.
- Raw FBX topology preparation through temporary imports without changing the original FBX; conservative full-loop and disconnected-small-part experiments.
- Optional strict hard-edge preservation. LOD Gen enables it by default; existing direct simplifier/pipeline callers opt in. Protected triangles and patch interfaces are validated before and after correction. No native ABI or distributed binary changes.
- Known protected-face floors prevent futile aggressive retries. Eligible preceding source-derived geometry can cap increasing density; it is cloned and remeasured, never recursively simplified.
- PR review fixes preserve edited working sources and preflight bindings during regeneration. Clearing previous generated LODs, prefab unpacking and generation share one Undo transaction; cancellation or an exception restores previous objects, mesh ownership and context entries, and releases pending meshes. The tool displays the failure and repaints. Regression cases cover working-quad/RGBA regeneration, cancellation between renderers/levels, and both cancellation and failure after clearing previous generated levels.
- RGBA fitting shares variables across equal-color render duplicates connected through unambiguous adjacent edges in one material slot. Disconnected contacts, ambiguous adjacency and material boundaries remain separate and conservatively pinned. Region pinning happens once per projection pass; identical mesh arrays bypass repeated whole-array comparisons. QSlim's experimental worker reports an empty prepared source instead of dividing by zero.
- Rejected varying-color fits now try area-weighted redistribution of the same source barycentric samples. Shared variables, pinned seams, Float/HDR/Color32 storage and per-channel RMS/maximum gates remain enforced. Successful ordinary fits skip the extra trials. On the actual tent's unprotected LOD1, maximum-channel surface RMS improves from 0.05224 to 0.04638 (11.2%); protected outputs and other tested outputs retain their previous fields. One GPU view worsens while the worst-view error improves; categorical-paint semantics remain unverified.
- Reproducible Unity GPU evaluation on eight actual project FBX meshes and an experimental libigl QSlim comparison. QSlim is a test/tooling experiment, not a production dependency or selected backend.

## Verified snapshot and limitations

The post-review baseline passed **162 selected LOD and collision EditMode tests**, with zero failures or skips, on Unity 6000.2.6f2. All eight actual FBX models were freshly recaptured after the Undo, RGBA-seam and performance fixes; no preceding GPU captures were reused. Both FBX compilation configurations, identifier and tool-dependency checks pass. Earlier 112/113/155-test runs remain historical evidence in [the evaluation](LOD_PROJECT_VISUAL_EVALUATION.md). These are selected checks, not a complete package-suite claim.

All 3019 authored hard-edge segments are retained at both generated levels, with zero missing protected face occurrences or patch interfaces. All 1660 measured regional correction checks pass. Nevertheless, strict protection currently reaches only 5 of 16 triangle budgets and only 2 of 8 LOD2 budgets. Equal adjacent counts can be a genuine protected-geometry limit.

The full eight-model RGBA comparison also passed 162 selected tests. Final verification passed **166 selected tests** with a fresh tent GPU capture, including rollback when a reducer returns failure and accurate preparation failure/cancellation statuses. Failed renderers abort the transaction rather than committing missing surfaces; preparation never reports success after failure or cancellation. Seven other captures retain the full comparison's lineage: their resampling trials all lost, and the final optimization only skips those trials. Their recorded timings/trial counts belong to that full experiment. Final artifacts: `_results~/lod-iteration-closure-20261010/final-color-fallback/`; test results: `final-preparation.xml` in its parent directory.

| Project mesh | LOD0 | Protected LOD1 | Protected LOD2 | LOD2 target |
| --- | ---: | ---: | ---: | ---: |
| Park Bench B | 9334 | 3112 | 2424 | 1038 |
| Park Bench A | 3832 | 1269 | 416 | 426 |
| Fire Shield | 1850 | 1502 | 1462 | 206 |
| First Aid Kit | 2886 | 1852 | 1714 | 321 |
| M84 grenade | 2040 | 2026 | 2026 | 227 |
| Rock | 1010 | 336 | 112 | 113 |
| Rainbow tent roof | 560 | 284 | 284 | 63 |
| Wrench | 612 | 468 | 432 | 68 |

Preserving each original crease segment is too conservative for useful reduction on several meshes. Bench B's worst silhouette mismatch worsens from 2.372% to 7.456%, despite retaining more triangles. Vertex-color RMS can improve while categorical paint boundaries still visibly interpolate incorrectly. Budget mode reports exceeded quality guides rather than treating them as certified acceptance limits. Full-loop reduction has not produced a practical gain on this project dataset.

Detailed implementation: [LOD controls and operators](LOD_FULL_LOOPS.md). Research: [topology and libraries](LOD_TOPOLOGY_RESEARCH.md). Test evidence, reproduction commands and capture lineage: [real project evaluation](LOD_PROJECT_VISUAL_EVALUATION.md). Workstation-only post-review artifacts are under `_results~/lod-iteration-closure-20261010/final-eight-models/`; copied project models and experimental Python dependencies are intentionally outside the package.

![Actual project models: source, unprotected LOD2 and protected LOD2](LOD_PROJECT_OVERVIEW.png)

![Measured triangle counts, retained hard segments and normal errors](LOD_HARD_EDGE_ERRORS.png)

![Actual tent views and RGBA errors before/after the fallback](LOD_RGBA_RESAMPLING.png)

The overview/chart retain the final capture lineage above; their LOD2 fields/counts remain identical to the post-review baseline. The RGBA figure compares that baseline with the final tent capture. Source hashes, feature/region checks and unchanged source PNG checks: `final-color-fallback/audit.json`. Integration PR: [#229](https://github.com/SashaRX/UnityMeshLab/pull/229).

## Next iteration, in order

Priority after the real-bench generalization check: preserve SmallBench's thin
frame and source RGB/alpha at approximately 1/9, and support patch correction
with shared render corners/material-separated meshes. Validate the weak
fourteen-view GPU cases before accepting improvements from the CPU guide.
Retain the university/top-view and author LOD2 controls; diagnose the new
protected LOD1 budget failures separately.

Then, after the partial paint-diagonal correction, allocate coarse tent
patches along the remaining violet/blue paint regions at the same 1/9
budget, retained render-vertex limit and 64px footprint. A single moved roof
control point has already failed the silhouette check; compare coordinated patch
replacements against all views and source UV2 before enabling them. Profile actual
rendering/buffer cost; matching paint/shading/UV seams must remain separate, and
further approximate sharing needs stronger validation than the CPU guide alone.
Extend UV2/chart-boundary and filtered lightmap checks to more assigned project instances.
Thin-strip distortion on the fire shield remains a geometry follow-up, with the
authored 303-triangle control kept visible. Exercise complete materials/assemblies
and LOD transitions after those checks. Shared bindings currently support only
unmipped full-HDR atlases.
Keep lighting/color fidelity ahead of exact normal-region matching for LOD2.

1. Native crease/junction constraints and the matched-count comparison are implemented and verified as an optional experiment. Remaining work in this stage: diagnose low-budget coverage failures, quality regressions and expensive retries; keep the strict/coarsened controls and rebuild native changes through CI.
2. Local screen diagnostics and an optional CPU guide for candidate ranking, attribute-correction refusal and whole-small-component removal are implemented. See [screen-guided selection](LOD_SCREEN_GUIDED_SELECTION.md) for the fresh comparison and remaining limits. Next: calibrate thresholds at actual screen sizes, profile extra cost and address Bench A/Fire Shield thin details and the tent's RMS-versus-boundary tradeoff. Categorical labels and original-material acceptance remain unverified.
3. Exercise original materials, normal maps, complete assemblies and LOD transitions in the project's render pipeline. Separate geometric silhouette, shading and color errors in the report.
4. Revisit full-loop removal on irregular project topology. Keep source provenance and topology/winding/intersection validation; require a measured reduction benefit before changing defaults.
5. Reconsider QSlim as an additional candidate only after equivalent attribute, topology, crease and quality checks. Any necessary native plugin change must be rebuilt through `build-native.yml`.

Keep every stage independently reviewable. Preserve working LOD0, source FBX bytes, material slots, owned-mesh cleanup, cancellation and Undo/LODGroup restoration. Do not label the system complete until the factor-three budgets and visual acceptance hold on the real project dataset.
