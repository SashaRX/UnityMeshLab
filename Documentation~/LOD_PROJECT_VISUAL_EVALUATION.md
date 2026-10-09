# Real project LOD evaluation — 2026-10-09

## Step 5: strict authored hard-edge preservation — 2026-10-10

The current experiment is `../_results~/lod-hard-edges-final-20261010/`. It compares the same eight copied FBX meshes through LOD Gen's shared pipeline, with Balanced three-candidate selection and verified normal/RGBA correction. New simplifications start independently from working LOD0, targeting LOD1 1/3 and LOD2 1/9. The source hashes, selection, transforms, GPU/shaders and near/far settings match Step 4. The comparison is production meshoptimizer with versus without the new constraint; experimental QSlim remains unchanged.

**Preserve Hard Edges** defaults on in LOD Gen; legacy pipeline/direct simplifier callers opt in. Exact-position edge adjacency and discontinuous authored endpoint normals identify source crease segments, including pairs across material slots. UV/color splits and isolated corner contacts alone do not. Both incident triangles form a retained belt; ambiguous non-manifold, wrong-winding or coincident adjacency is retained conservatively. The remaining triangles are simplified with locked patch borders using the existing native ABI. Validation checks oriented source crease segments with normals on both sides, protected face multiplicity and exact patch interfaces again after correction. Rejected output keeps an owned source copy with explicit diagnostics. These are current authored normal boundaries, not recovered DCC smoothing-group IDs; exact coincidence cannot recover original control-point identity.

The initial experiment in `../_results~/lod-hard-edges-20261010/` passed its feature checks but exposed a budget-search problem: when protected faces alone already exceed the goal, retries still raised error tolerance and removed normal/RGBA costs. Those attempts could damage unprotected geometry without making the target attainable. In the final policy, that known floor disables aggressive retries. Each strategy keeps its own emphasis costs and requested Target Error, and over-budget strategies rank by measured quality; a budget-reaching result still wins over every over-budget result. First Aid Kit LOD2 consequently improves from the initial strict run's 35.835 to 12.099 degrees normal RMS for 1714 instead of 1650 triangles. Tests cover both this retry policy and its priority relative to a reachable budget.

The full eight-model follow-up is `../_results~/lod-hard-edges-v2-20261010/`. It also exposed a hierarchy problem: the tent's three independent far strategies were all denser (294/294/302) than its protected LOD1 (284). Protected budget Triangles now caps growth for a smaller target when the previous requested profile is eligible and the retained-part surface is unchanged. If all new strategies exceed that count, the previous source-derived geometry is cloned as an additional candidate, remeasured against LOD0 and corrected under the current profile; it is never fed into another collapse pass. The final regression/GPU rerun uses the tent-only dataset. The other seven final captures are retained from the full follow-up: their already-selected far candidates are below the preceding count, so the added density constraint leaves their selection and fitting inputs unchanged. Both full and focused XMLs are retained in the final directory; `audit.json` compares hashes, baseline counts, feature checks, regional gates and both-level density.

This is deliberately conservative protection of original crease **segments**, rather than an operator that coarsens a feature polyline while retaining its shape and shading discontinuity. It can stop well above the requested count or give identical consecutive LOD counts. Those are reported missed budgets, not reused ratios or successful factor-three reduction. Normal weights, final-level color relaxation and six-view relative ranking do not establish original-material parity or a universal visual improvement. The GPU sheets retain all actual counts and errors, including regressions. They use two diagnostic 384-pixel views, optional original albedo and authored vertex color; complete assemblies, original URP materials/normal maps and gameplay transitions are outside this experiment.

Reproduce with a GPU using the copied dataset:

```text
Unity -batchmode -projectPath <isolated-project> -runTests -testPlatform EditMode
  -testFilter "LodHardEdgeTests;LodSmoothingRegionTests;LodQslimTransferTests;LodAttributeCorrectionTests;LodQualitySelectionTests;LodGenerationToolTests;LodLoopSimplifierTests;LodSmallPartsTests;LodProjectVisualQualityTests"
  -testResults <output>/final.xml -logFile <output>/final.log
  -meshlabLodBudget -meshlabLodHardEdges
  -meshlabLodProjectCases <output>/cases.json -meshlabLodVisualOutput <output>
python -X utf8 Tools~/render_lod_project_quality.py '_results~/lod-hard-edges-final-20261010'
```

Final verification: **112 targeted Unity EditMode tests passed on the full eight-model follow-up**, then **113 passed on the final regression + tent GPU rerun**, with no failures or skips. The two FBX compilation configurations, Python syntax, metadata and whitespace checks pass. Distributed native plugins, native source and package dependencies are unchanged. These are targeted results, not a full package-suite claim.

The combined final artifact has 16 protected results and their 16 unprotected references. All **3019 original authored hard segments** across eight sources are retained at both levels, with zero missing protected triangle occurrences or patch interfaces and no rejected-candidate source fallbacks. The unprotected LOD2 references fail exact retention of 2917 source segments; this counts original segments, not whole visible crease curves. All 1660 regional RMS/maximum pairs pass their correction non-regression checks. Source FBX hashes and source GPU PNG pixels match Step 4 exactly.

Only five of sixteen protected outputs reach the requested budgets: both levels of Bench A and the rock, plus Bench B LOD1. All final protected LOD2 counts are at/below LOD1; the tent reuses 284 source-derived triangles after its current native strategies exceed that density. Identical counts on the grenade and tent reflect feature/boundary limits, not repeated ratio inputs.

| Project mesh | LOD0 | Protected LOD1 | Protected LOD2 / requested | Source hard segments | LOD2 normal RMS unprotected / protected, degrees | Worst GPU silhouette unprotected / protected |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Park Bench B | 9334 | 3112 | 2424 / 1038 | 640 | 12.39 / 24.27 | 2.372% / 7.456% |
| Park Bench A | 3832 | 1269 | 416 / 426 | 0 | 37.76 / 37.76 | 6.881% / 6.881% |
| Fire Shield | 1850 | 1502 | 1462 / 206 | 738 | 49.67 / 12.71 | 23.475% / 3.593% |
| First Aid Kit | 2886 | 1852 | 1714 / 321 | 541 | 37.19 / 12.10 | 8.094% / 4.099% |
| M84 grenade | 2040 | 2026 | 2026 / 227 | 839 | 46.69 / 0.01 | 10.943% / 0.000% |
| Rock | 1010 | 336 | 112 / 113 | 0 | 22.56 / 22.56 | 7.959% / 7.959% |
| Rainbow tent roof | 560 | 284 | 284 / 63 | 87 | 25.93 / 10.29 | 2.875% / 0.000% |
| Wrench | 612 | 468 | 432 / 68 | 174 | 52.62 / 21.81 | 23.609% / 2.520% |

This is not a universal quality win or a matched-complexity comparison. Colored Bench B LOD2 gets denser (2424 versus 1036) while normal RMS worsens (24.273 versus 12.391 degrees) and worst silhouette mismatch worsens (7.456% versus 2.372%); sampled RGBA RMS improves (0.05346 versus 0.07849). Hard-edge protection alone does not preserve unprotected soft components, categorical paint semantics or original material appearance. The tent improves sampled RGBA RMS from 0.07627 to 0.06370, but its images still expose visible field/interpolation differences. Feature-polyline coarsening and stronger thin/soft-part/visual acceptance remain separate unfinished work.

See `report.md`, `project-overview.png`, `hard-edge-errors.png`, all per-model front/oblique/compact sheets and full `*-metrics.json` values in the final directory. `run-settings.json`, `cases.json`, `tent-cases.json`, both XMLs and `audit.json` retain run provenance.

## Step 4: connected smoothing-region correction — 2026-10-10

The latest artifacts are `../_results~/lod-smoothing-20261010/`, using the same copied eight-FBX dataset, previously exported QSlim geometry and matched actual meshoptimizer triangle targets. `report.md` adds source region counts, linked normal duplicates, pinned mixed/unmapped faces, missing source regions and incomplete correspondences. Each metrics JSON retains before/after angular RMS and maximum for every region. The separate global metrics and Unity GPU captures still expose shading tradeoffs.

[LodSmoothingRegions](../Editor/LodSmoothingRegions.cs) derives connected regions from working LOD0 normal continuity across unambiguous geometric edge pairs, independently per material slot. UV/color/tangent discontinuities do not split normal regions. Hard normal discontinuities, corner-only contacts, ambiguous edge incidence, wrong winding and coincident opposite triangles do not join them. These inferred regions represent the current source shading, not original DCC smoothing-group IDs or an angle-based normal recalculation.

Four interior probes assign each reduced face to one source region. Mixed/unmapped faces and vertices shared by different regions are pinned. Only adjacent output faces assigned to the same source region, with continuous geometry-projected source normals at both edge endpoints, link normal variables across duplicated render vertices. Therefore a transfer-created normal mismatch at a smooth UV seam can be corrected without welding geometry or UVs. Source discontinuities keep separate variables. Normal fitting projects inside the assigned source region in both directions; RGBA keeps its independent existing fit. A pin or unsafe hemisphere trial applies to all linked copies.

The existing global normal acceptance gate remains. In addition, a cached bidirectional regional correspondence checks four area-weighted interior samples for RMS and vertices/edge midpoints for maximum angle. Every measured region must retain or improve both errors within 0.0001 degrees, relative to the pre-fit baseline and any preceding accepted trial. A small detail's regression cannot hide behind a large surface improvement. Incomplete correspondences pin the affected region; missing source regions are diagnosed rather than treated as zero-error preservation. Fitting and verification use different quadratures, but they are sampled checks rather than certified bounds or an independent holdout.

Geometry, triangle indices/counts, material slots and UVs remain unchanged by correction. This stage does not reconstruct hard edges already deleted during budget-first reduction, recover every internal crease from a connected region label, or certify detection of every small crossed region with four probes. Meshoptimizer permissive collapse and geometry-only QSlim still require separate feature constraints if strict geometric crease preservation is requested. The normal-group correction is enabled through the existing **Correct Normals / RGBA** option; production native binaries and ABI are unchanged.

Regression coverage adds shared normal fitting across a smooth UV seam with initially inconsistent transferred normals, hard-boundary separation, corner contacts, ambiguous and coincident edges, material isolation, crossed-region pinning, per-region rejection despite a large-region improvement, and cancellation without input mutation. Reproduce the preceding QSlim comparison with the same copied source/output JSONs and the final code; use `-meshlabLodBudget -meshlabQslimCompare` and include `LodSmoothingRegionTests` in the targeted EditMode filter. No external QSlim rerun is required because positions, indices and matched targets are unchanged.

Build the complete capture sheets/report and compare global normal RMS with the preceding stage using:

```powershell
python -X utf8 Tools~/render_lod_project_quality.py '_results~/lod-smoothing-20261010' --smoothing-baseline '_results~/lod-qslim-20261009'
```

The renderer verifies matching source hashes and actual triangle counts. `smoothing-errors.png` compares LOD2 angular RMS for both backends; `smoothing-before-after.json` retains both levels and front-view diagnostic shading errors. These previous/current comparisons are distinct from the regional pre-fit/post-fit acceptance checks in each capture.

Final verification: **102 targeted Unity EditMode tests passed, 0 failed, 0 skipped**, including all eight GPU cases and seven new smoothing regressions on the final implementation (`final.xml`). Both FBX compilation variants and metadata/whitespace checks pass. All 32 generated results (two backends, two levels, eight meshes) retain the preceding actual counts and measured GPU silhouettes; all 1,660 per-output regional RMS/maximum pairs pass their pre-fit/post-fit non-regression checks. Seventeen of 32 normal fits are accepted. There are 2,033 linked render-vertex copies across those outputs; this is a constraint count, not a count of successfully repaired visible seams. Thirty-eight per-output regions have incomplete facing-compatible correspondence and are pinned; source-region absence is reported separately. Source FBX hashes remain unchanged.

The stricter correction is not a universal global-error improvement. Meshoptimizer First Aid Kit LOD2 rejects the former fit: global normal RMS is 37.186 rather than 34.990 degrees. Bench A LOD2 similarly retains 37.760 rather than 25.320 degrees. Shared-variable fitting changes the rock LOD2 compromise: meshoptimizer RMS is 22.559 versus the preceding independently fitted 12.048 degrees, while QSlim improves slightly from 15.948 to 15.733 degrees. The previous/current graph displays these regressions rather than presenting acceptance as a visual-quality win. Choosing between stronger continuity, regional error protection and global appearance remains an explicit quality tradeoff; geometry feature constraints and original-material evaluation are still separate work.

## Step 3: libigl QSlim matched-budget comparison — 2026-10-10

The complete comparison is `../_results~/lod-qslim-20261009/`: `project-overview.png` contains the same eight real project meshes as the preceding stages, `qslim-errors.png` plots distance and silhouette errors, and `report.md` retains both LOD levels, both GPU views, normal/RGBA/UV measurements, timings, provenance and connectivity diagnostics. Each compact sheet shows LOD0, meshoptimizer LOD1, QSlim LOD1, meshoptimizer LOD2 and QSlim LOD2. These are the largest eligible meshes per FBX, not complete assemblies; the tent case is its roof mesh.

The baseline is the current Balanced meshoptimizer pipeline with accepted normal/RGBA correction. QSlim uses the pinned official **libigl Python bindings 2.6.3**, isolated under `_results~/qslim-python-2.6.3/`. Both levels start independently from the same verified LOD0. QSlim targets the baseline's **actual** counts, separately per material slot, after welding by verified original FBX control-point IDs. Equal positions alone never establish connectivity. QSlim uses geometry-only 3D quadrics, followed by birth-face attribute-chart reconstruction, source projection and the same optional verified normal/RGBA correction. All UV channels/dimensions, color formats and source/export hashes are checked. No production backend, native plugin source, ABI, binary or package dependency changes in this stage.

Geometry RMS is bidirectional nearest-surface distance normalized by LOD0 diagonal, using unrestricted same-material correspondence for both backends. Attribute transfer/validation uses separate corner-aware correspondence. GPU silhouette is XOR/union coverage, worst of two 384-pixel orthographic views. Shading uses diagnostic headlight illumination and original albedo where supplied; it does not reproduce the project's URP/normal-map appearance. These are sampled estimates and views, not certified surface bounds or a universal quality grade.

| Model | LOD2 triangles meshopt / QSlim | Geometry RMS meshopt / QSlim, % diagonal | Normal RMS meshopt / QSlim, degrees | Worst GPU silhouette meshopt / QSlim | Worst GPU shading RMS meshopt / QSlim | Maximum-channel RGBA RMS meshopt / QSlim |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Park Bench B, authored RGBA | 1036 / 1035 | 0.069 / 0.037 | 10.78 / 8.95 | 2.372% / 1.648% | 0.0200 / 0.0184 | 0.07849 / 0.07574 |
| Park Bench A | 416 / 416 | 0.248 / 0.128 | 25.32 / 28.85 | 6.881% / 5.063% | 0.0759 / 0.0600 | constant |
| Fire Shield | 204 / 221 | 0.707 / 2.068 | 49.08 / 82.49 | 23.475% / 59.775% | 0.1102 / 0.2401 | absent |
| First Aid Kit | 318 / 318 | 1.035 / 0.255 | 34.99 / 21.81 | 8.094% / 2.091% | 0.1750 / 0.0986 | absent |
| M84 Stun Grenade | 216 / 219 | 0.854 / 1.699 | 45.32 / 68.68 | 10.943% / 32.988% | 0.1364 / 0.2713 | absent |
| Mountain Rock Small 01 | 112 / 112 | 1.157 / 0.552 | 12.05 / 15.95 | 7.959% / 4.456% | 0.0870 / 0.0751 | constant |
| Rainbow Tent | 62 / 62 | 0.296 / 0.649 | 25.76 / 25.57 | 2.875% / 17.068% | 0.0872 / 0.0799 | 0.07627 / 0.09339 |
| Wrench | 68 / 67 | 0.990 / 0.860 | 47.81 / 42.66 | 23.609% / 18.259% | 0.2153 / 0.2186 | absent |

QSlim improves both LOD2 geometry distance and the measured silhouette on five of eight meshes, but worsens them on Fire Shield, grenade and tent. The First Aid Kit is the strongest improvement. Normal and shading metrics do not always follow shape: Bench A and the rock have higher angular RMS, and the wrench has slightly higher GPU shading RMS. Vertex-color loss remains a selection concern: Tent LOD2 RGBA RMS increases from 0.07627 to 0.09339; colored Bench B LOD1 increases from 0.04982 to 0.07235 despite better shape. A correction accepted relative to its own pre-fit result does not guarantee a better result than the other backend.

Fourteen of sixteen QSlim outputs reach the matched target. Fire Shield LOD2 stops at 221 versus 204, grenade LOD2 at 219 versus 216. No arbitrary face deletion forces these budgets. The Python wrapper omits the native success boolean, so attainment is determined independently from output face counts. Birth-face indices reconstruct attribute charts; when no facing-compatible transfer point exists, unrestricted nearest projection stays within the same birth chart and its corner count is recorded. Wrench LOD2 uses this fallback for one corner. It does not select a different chart based on better color agreement.

Fire Shield initially has two zero-area source triangles; the explicit `--remove-degenerate-faces` preparation removes only those faces and remaps birth-face indices to the original triangle list. This changes its Euler characteristic from 18 to 17 and closed boundary-loop count from 4 to 5. All sixteen native QSlim outputs preserve components, Euler characteristic and boundary-loop counts **relative to the prepared input**, and are edge/vertex manifold, consistently wound and free of zero-area faces in the diagnostic. This is measured before render-vertex attribute splitting, independently per material slot. Intersection blocking is disabled; these invariants do not certify absence of intersections, preserve authored quad flow, or establish equivalent meshoptimizer topology checks.

Verification: **95 targeted Unity EditMode tests passed, 0 failed, 0 skipped**, including all eight actual-project GPU comparisons and three new chart-transfer regressions. Both FBX C# compilation variants pass. This does not establish complete package-suite status. The images show actual severe QSlim failures as well as improvements. The current evidence supports evaluating QSlim as an additional candidate with geometry, silhouette, attribute and budget gates; it does not justify replacing the production backend by default. Complete-loop reduction remains a separate operator.

Reproduction uses an isolated Unity project with copied FBX assets/importer metadata and the worktree linked as a package. In [LodProjectVisualQualityTests](../Tests/Editor/LodProjectVisualQualityTests.cs), first run the eight-case capture fixture with `-meshlabQslimExport`, `-meshlabQslimBaseline` pointing to the preceding correction results, `-meshlabLodProjectCases` pointing to this run's `cases.json`, and `-meshlabLodVisualOutput` pointing to the output directory. Install the pinned Python wheel only in the ignored experiment folder, then run:

```powershell
python -X utf8 -m pip install --target '_results~/qslim-python-2.6.3' --no-deps libigl==2.6.3
python -X utf8 Tools~/compare_lod_qslim.py '_results~/lod-qslim-20261009' --library '_results~/qslim-python-2.6.3' --remove-degenerate-faces
python -X utf8 Tools~/compare_lod_qslim.py '_results~/lod-qslim-20261009' --summarize-existing
```

Run the same Unity fixture with `-meshlabLodBudget -meshlabQslimCompare` and the same cases/output flags, on a GPU-capable batch Editor without `-nographics`. Then build the sheets, graph and report:

```powershell
python -X utf8 Tools~/render_lod_project_quality.py '_results~/lod-qslim-20261009'
```

Upstream references: [QSlim API](https://github.com/libigl/libigl/blob/main/include/igl/qslim.h), [QEM implementation](https://github.com/libigl/libigl/blob/v2.6.0/include/igl/qslim.cpp), [Python wrapper](https://github.com/libigl/libigl-python-bindings/blob/main/src/qslim.cpp), [pinned Python release](https://pypi.org/project/libigl/2.6.3/). The exact installed native binary hashes and Python/NumPy versions are recorded in each output JSON; the linked source tag is an implementation reference, not a claim of a verified wheel source commit.

## Step 2: verified normal and RGBA surface correction

The latest comparison is `../_results~/lod-attribute-correction-20261009/`: `project-overview.png` shows actual imported project meshes; `attribute-errors.png` plots measured field errors; each model has full/compact before-after sheets and JSON metrics. `report.md` retains channel maxima/RMS, acceptance decisions, candidate tables and both GPU views. Reproduce with `-meshlabLodBudget -meshlabLodAttributeCorrection`, the same eight-case copied-FBX dataset, and the renderer below.

Balanced geometry selection remains three source candidates at independent 1/3 and 1/9 targets. Correction runs only after topology selection. Every before-after pair asserts identical positions, indices per material slot, UV2 and vertex attribute descriptors. All 16 results reach their targets. Each source FBX hash matches its recorded project copy. No synthetic geometry/colors are substituted for this comparison; model selection remains the largest eligible mesh per FBX, not the complete assembly.

Surface fitting is useful because a nearest-vertex transfer would largely reproduce attributes already retained from LOD0. Sparse regularized least squares fits normal XYZ and RGBA over nine equal-area face-interior samples in both projection directions, with geometry/winding-based same-slot correspondence. Three blend strengths are measured against LOD0. Normal RMS must improve without increasing authored maximum angle; each RGBA channel's sampled RMS and maximum must not worsen, with at least one channel improving. Normal and color acceptance are independent. Rejected corrections retain their channel. Float/HDR and Color32 formats, alpha, existing duplicated hard attribute seams and tangent handedness are covered by regression tests. See [LOD_FULL_LOOPS.md](LOD_FULL_LOOPS.md) for scope and numerical tolerances.

15 of 16 normal fits are accepted; colored Bench B LOD1 retains its original normals. Three of four varying-color results accept RGBA correction; Tent LOD1 retains its original RGBA because no tested fit passes the per-channel gate. Constant/absent color channels are not counted as color improvements. The selected candidate tables record pre-correction scores; the final mesh's displayed quality score is recalculated after accepted fitting.

| Model | LOD2 tris, before = after | Normal surface RMS before / after, degrees | Worst GPU shading RMS before / after | Maximum-channel RGBA surface RMS before / after |
| --- | ---: | ---: | ---: | ---: |
| Park Bench A | 416 | 37.760 / 25.320 | 0.1423 / 0.0759 | constant |
| Park Bench B, authored RGBA | 1036 | 13.522 / 10.777 | 0.0202 / 0.0200 | 0.08084 / 0.07849 |
| First Aid Kit | 318 | 37.186 / 34.990 | 0.1727 / 0.1750 | absent |
| Fire Shield | 204 | 50.681 / 49.081 | 0.1112 / 0.1102 | absent |
| M84 Stun Grenade | 216 | 46.695 / 45.318 | 0.1355 / 0.1364 | absent |
| Mountain Rock Small 01 | 112 | 28.297 / 12.048 | 0.1151 / 0.0870 | constant |
| Rainbow Tent | 62 | 27.283 / 25.760 | 0.0958 / 0.0872 | 0.13060 / 0.07627 |
| Wrench | 68 | 52.622 / 47.811 | 0.2883 / 0.2153 | absent |

Bench B LOD1's varying red-field RMS improves from 0.05141 to 0.04982 without increasing any RGBA channel's maximum; its normals stay unchanged. Tent LOD2's maximum-channel RMS decreases by about 42%. These field checks do not certify categorical color-mask semantics. Surface normal quadrature differs from fitting, but this is not an independent holdout dataset or certified error bound.

The two GPU regressions are retained explicitly: First Aid Kit LOD2 shading RMS increases from 0.1727 to 0.1750 and grenade LOD2 from 0.1355 to 0.1364 despite lower surface angular RMS. Both diagnostic GPU silhouettes are unchanged because geometry is identical. Correction cannot restore discarded geometry or make all ninefold reductions visually acceptable. Requested normal/color profile limits remain diagnostics in budget-priority mode; accepting an improvement does not imply the final mesh satisfies those absolute limits. Original URP materials/normal maps, perspective distance transitions, animated poses and full-assembly evaluation remain outside this harness.

Final verification: **92 targeted Unity EditMode tests passed, 0 failed, 0 skipped**, including all eight actual-project GPU comparisons, seven new correction regressions, and existing LOD budget/topology/lifecycle checks. The initial run completed its model captures but exceeded the fixture's old 180-second timeout; the opt-in eight-FBX fixture now has a 600-second limit for the additional fitting/verification work, and the complete rerun passes. Both FBX C# compilation variants, meta coverage and whitespace checks pass. Native plugin source/ABI/binaries are unchanged by this stage. Complete package-suite status is not claimed.

## Step 1: fixed-budget quality candidate selection

The latest artifacts are `../_results~/lod-quality-selection-20261009/`. The same eight FBX hashes are checked, with only LOD0, LOD1 and LOD2. Fast is the previous first budget-reaching strategy; Balanced independently compares three source strategies at the same 1/3 and 1/9 targets. High's five strategies are covered by regression tests. The default LOD Gen quality is now Balanced. Every candidate's native probe count and measured score components are recorded in JSON and `report.md`; generation notes identify the selected strategy.

All 16 Balanced LODs reach the requested budgets and have finite rankings; 12 select a variant other than the first. This count does not imply 12 distinct topologies or improvement in every visual metric. The ranking combines six 128-pixel CPU silhouette views and bidirectional area RMS geometry, normal, protected UV and RGBA errors. Profile weights/caps control the normal/color contributions. It is a relative heuristic; the independent 384-pixel GPU views below expose tradeoffs.

| Model | LOD2 Fast / Balanced tris | Worst GPU silhouette Fast / Balanced | Worst GPU shading RMS Fast / Balanced |
| --- | ---: | ---: | ---: |
| Park Bench A | 422 / 416 | 10.409% / 6.881% | 0.109 / 0.142 |
| Park Bench B, authored RGBA | 1036 / 1036 | 2.598% / 2.372% | 0.027 / 0.020 |
| First Aid Kit | 320 / 318 | 6.278% / 8.094% | 0.110 / 0.173 |
| Fire Shield | 204 / 204 | 23.475% / 23.475% | 0.111 / 0.111 |
| M84 Stun Grenade | 216 / 216 | 10.943% / 10.943% | 0.136 / 0.136 |
| Mountain Rock Small 01 | 112 / 112 | 7.959% / 7.959% | 0.115 / 0.115 |
| Rainbow Tent | 62 / 62 | 2.875% / 2.875% | 0.096 / 0.096 |
| Wrench | 68 / 68 | 23.609% / 23.609% | 0.288 / 0.288 |

LOD1 worst silhouette improves for the rock (3.027% to 1.932%), tent (2.425% to 0.566%) and colored bench (0.753% to 0.704%). Wrench LOD1 shading RMS improves from 0.088 to 0.062 while worst silhouette increases slightly, 12.856% to 13.169%.

The First Aid Kit counterexample is retained: lower aggregate source-local error does not guarantee improvement in a particular camera/material view. Bench A's silhouette improves while its shading worsens. Six low-resolution masks, face-interior RMS samples and their weights cannot certify all views. At this stage output attributes remain retained source values; no corrective normal/color fitting or original URP/normal-map validation has been performed. Those are subsequent stages rather than claims attached to this selection change.

Actual FBX testing exposed an absolute-epsilon normal bug: `Vector3.normalized` zeroed some valid tiny face normals, producing infinite scores. Geometric normals now normalize source-scale-relative edges. Triangle ranking excludes truly zero-area faces on both sides of correspondence, while missing nonzero surface still produces invalid metrics and strict loop validation still rejects degeneracy. Regression tests cover small valid faces, lost surfaces, face-order-independent double-sided silhouette and normal RMS on matching geometry.

Final targeted Unity result: **85 passed, 0 failed**, including the eight-model Fast/Balanced GPU comparison, default two-level/Balanced settings, three/five candidate evaluation, budget precedence, area RMS, immutable source data, locked boundaries, and cancellation between variants with destruction of the retained candidate. Both FBX compile variants and meta/whitespace checks pass. Native source/binaries are unchanged. This targeted result does not establish complete package-suite status.

## Current planned chain: LOD0, LOD1, LOD2

Only two generated levels are planned. LOD0 is the source, LOD1 targets 1/3 of LOD0, and the final LOD2 targets 1/9. The tool already defaults to a generated count of two; the test now asserts this. The corrected capture run in `../_results~/lod-budget-two-levels-20261009/` uses exactly this chain and applies the far-profile endpoint to LOD2: requested Target Error 0.3, Normal Weight 0.5, Color Weight 0.25, RGBA cap/diagnostic 0.10 and normal angle 30 degrees.

| Project mesh | LOD0 | LOD1 | Final LOD2 |
| --- | ---: | ---: | ---: |
| Wrench | 612 | 204 | 68 |
| First Aid Kit | 2886 | 962 | 320 |
| M84 Stun Grenade | 2040 | 669 | 216 |
| Park Bench A | 3832 | 1276 | 422 |
| Fire Shield | 1850 | 612 | 204 |
| Mountain Rock Small 01 | 1010 | 336 | 112 |
| Park Bench B, authored RGBA | 9334 | 3110 | 1036 |
| Rainbow Tent, authored RGBA | 560 | 186 | 62 |

All 16 generated meshes reach the requested budgets. The focused Generation Tool / real-project run passed all 13 EditMode cases; compile checks passed both FBX define variants. LOD3 captures below are an earlier extra-level experiment and are outside the planned chain.

## Prior experiment: three generated levels with budget priority

The prior extra-level run is `../_results~/lod-budget-20261009/`; it is retained as a historical experiment. Generation uses independent LOD0 fractions 1/3, 1/9 and 1/27 with triangle budget priority enabled. All 24 generated meshes reach their requested target; native triangle granularity may undershoot it. The final native settings, source distance, normal/RGBA errors and profile warnings are saved per capture. Colors and other source channels are retained; their approximation limits no longer restore a dense source in this mode.

| Project mesh | LOD0 | LOD1 | LOD2 | LOD3 | LOD3 silhouette mismatch, worst of two views |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wrench | 612 | 204 | 68 | 19 | 67.61% |
| First Aid Kit | 2886 | 962 | 318 | 106 | 10.25% |
| M84 Stun Grenade | 2040 | 669 | 216 | 73 | 27.11% |
| Park Bench A | 3832 | 1276 | 423 | 132 | 21.24% |
| Fire Shield | 1850 | 612 | 204 | 66 | 56.10% |
| Mountain Rock Small 01 | 1010 | 336 | 112 | 36 | 12.74% |
| Park Bench B, authored RGBA | 9334 | 3110 | 1036 | 344 | 12.26% |
| Rainbow Tent, authored RGBA | 560 | 186 | 62 | 20 | 18.22% |

This fixes the ineffective triangle reduction, but it does not solve visual preservation at extreme budgets. Wrench, Fire Shield and bench supports lose thin features at LOD3; the kit's texture/shading also changes. These are same-size diagnostic captures, not approval at the intended gameplay distances. Native seam relaxation is used when simpler retries stall. Maximum normal correspondence errors can approach 180 degrees on strongly altered thin surfaces; this measurement alone does not establish flipped face winding.

Final targeted Unity EditMode result: **73 passed, 0 failed**, including all eight real project models, exact budget checks, RGBA/UV channel preservation, source immutability, cancellation and retained locked boundaries under permissive retries. Compile checks passed with and without FBX Exporter; metadata and whitespace checks passed. This is targeted LOD verification, not a claim that the complete package suite is green. Artifacts: `budget-final.xml`, `project-overview.png`, individual comparison sheets, float readbacks, JSON provenance and `report.md`.

The source assets are eight actual FBX files from `C:/LostInExtraction/fps-project/Assets`, copied into an isolated Unity project. No geometry or vertex colors were painted or generated for these cases. The capture harness is [LodProjectVisualQualityTests](../Tests/Editor/LodProjectVisualQualityTests.cs); comparison sheets are built by [render_lod_project_quality.py](../Tools~/render_lod_project_quality.py).

## Setup

Unity 6000.2.6f2, Built-in preview, Direct3D12, NVIDIA GeForce GTX 980 Ti. Each FBX retains its original importer metadata, with Read/Write enabled only on the copied asset. The recorded SHA-256 is asserted against the copied FBX before evaluation. The original project remains unchanged.

The largest mesh without a collision or lower-LOD name is selected per FBX. This is a mesh-level comparison, not an evaluation of complete multi-mesh/skinned assemblies. Generation uses `LodPipelineOps.Generate`, the shared LOD Gen path, with independent LOD0 budgets of 50% and 25%, Target Error 0.2, UV2 Weight 20, Normal Weight 1, Color Weight 1, Max Color Error 0.02, Max Normal Angle 15 degrees and Lock Border off. Full Loops evaluates five candidates when source polygons can be loaded.

Captures are orthographic, frontal and oblique, at 384×384 float pixels. The original albedo textures are supplied for Wrench and First Aid Kit. Other cases show diagnostic shading or authored vertex color. The shader uses headlight illumination, not the original URP material, normal maps, packed material maps or scene lighting.

## Initial triangle mode results

Silhouette mismatch is XOR coverage divided by union coverage. Values below are the worst of the two views; they are not a percentage of geometric volume or a general quality score. Shading RMS is the difference in linear RGB over common coverage, excluding a two-pixel boundary.

| Project mesh | LOD0 tris | LOD1 tris | LOD2 tris | LOD2 silhouette mismatch | LOD2 shading RMS |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wrench | 612 | 305 | 153 | 20.60% | 0.1259 |
| First Aid Kit | 2886 | 1442 | 720 | 2.57% | 0.0486 |
| M84 Stun Grenade | 2040 | 1019 | 506 | 8.22% | 0.0805 |
| Park Bench A | 3832 | 1916 | 958 | 4.99% | 0.0462 |
| Fire Shield | 1850 | 922 | 530 | 32.97% | 0.1082 |
| Mountain Rock Small 01 | 1010 | 504 | 252 | 4.13% | 0.0709 |
| Park Bench B, authored RGBA | 9334 | 9334 | 9334 | 0% | 0 |
| Fair Stall Rainbow Tent, authored RGBA | 560 | 560 | 560 | 0% | 0 |

The two colored cases retain LOD0 after the final RGBA guard rejects their reduced results. Zero error here means unchanged source retention, **not successful LOD generation**. An additional unchecked LOD2 control disables that final rejection while retaining native color costs; it reduces Park Bench B to 2332 triangles and the tent to 140, exposing visible field loss. The controls are not accepted protected LODs.

## Initial assessment

First Aid Kit keeps its overall silhouette reasonably close in both views; LOD2 visibly simplifies strap edges, corners and shading. Park Bench A is recognisable at 25%, but its thin frame and decorative curves suffer. The rock also remains recognisable, with measurable changes to its facets and outline.

Wrench and Fire Shield show conspicuous loss of thin features at 25%. Fire Shield misses its requested budget (463 triangles) even after producing an inferior silhouette. The triangle-mode native error bound alone does not guarantee visually acceptable thin-feature preservation. LOD1 is less destructive in this capture set, and a uniform 25% target is not appropriate for every prop.

The real RGBA cases show the cost of the current all-or-nothing color fallback: it protects the field but delivers no triangle reduction. Intermediate acceptable budgets or a validated attribute transfer/correction remain useful future work. These captures do not establish categorical-mask preservation.

Full Loops has no practical reduction in this set. Wrench contains only one original quad; the rainbow tent has 222 original quads but no removable loop passes the current constraints. Park Bench A and the rock have triangulated source polygons. First Aid Kit, M84, Fire Shield and Park Bench B fail source mesh-name correspondence in tagged import; their loop mode is not evaluated. This failure is recorded as a limitation, not reinterpreted as absence of quads.

## Validator correction exposed by the real cases

The initial surface validation sometimes sampled the other side of a color seam at shared corners/edges. Even an unchanged copied FBX could receive a large maximum RGBA error. Boundary correspondence now probes the source face's interior to choose the one-sided spatial limit, then measures the original point; it does not choose a triangle based on color agreement. A farther surface cannot replace the nearest hit just to obtain better attributes.

When vertex positions and connectivity are unchanged, the validator uses the known face correspondence directly. This also handles overlapping or very thin faces without an ambiguous nearest query. It continues measuring colors, normals and UVs, so changed attributes on identical geometry are still detected. A regression covers reordered triangles across a color seam and rejects changed RGB/alpha on identical geometry. Real-project captures also assert that a kept-source fallback validates against LOD0.

## Follow-up: raw FBX topology and validated triangle budgets

The follow-up artifacts are in `_results~/lod-next-20261009/`. The earlier tables remain the initial baseline. The same eight source FBX hashes are checked again; no source geometry or colors are authored for the follow-up.

The four apparent mesh-name failures were caused by compressed UV corner tags rather than mismatched FBX names. LOD source loading now copies the asset through AssetDatabase, disables mesh compression on that temporary import, obtains exact tagged corner IDs, and rebinds them to the verified compressed import and working mesh. The generic tagged-import integer tolerance is unchanged. Source files and compression settings are preserved, and temporary copies are removed on both success and failure.

First Aid Kit now loads its 966 original quads. Wrench and the rainbow tent still load 1 and 222 quads respectively. M84, both benches and the rock have no source quads. Fire Shield reaches topology validation and is refused for inconsistent polygon winding. There are no remaining mesh-name/tag failures in these eight cases. Full Loops still finds no acceptable complete-loop removal in this dataset; successful source reading does not establish practical loop reduction.

Triangle mode now compares color-weight variants and, when necessary, searches safer budgets against LOD0. Accepted candidates retain complete bidirectional sampled metrics; rejected candidates can stop as soon as a limit is exceeded. An already validated candidate can be reused for a farther level without simplifying it again, so independent searches cannot increase triangle count between levels.

| Authored-color mesh | LOD0 tris | Protected LOD1 | Protected LOD2 | Maximum sampled RGBA error | Requested budgets reached |
| --- | ---: | ---: | ---: | ---: | --- |
| Rainbow tent | 560 | 528 | 528 | 0.01273 | No |
| Park Bench B | 9334 | 9334 | 9334 | 0 | No, source retained |

Fast and High return the same protected counts on these two models. The tent achieves a modest 5.7% reduction instead of retaining the whole source, while the bench still has no reduced candidate passing the 0.02 maximum-color threshold. Equal LOD1/LOD2 counts here reflect a missed requested budget under the color constraint, not a repeated input ratio. Unchecked controls remain 140 and 2332 triangles and demonstrate why the color validator rejects the aggressive results.

The tent is additionally captured with a 90-degree X preview rotation to expose its surface rather than its thin edge. Camera-dependent pixel errors from that view must not be compared directly with the initial unrotated captures. The diagnostic shader and original-material limitations remain the same.

Verification: 53 selected Unity EditMode tests pass, including real GPU captures of all eight models, source reading with Off/Low/Medium/High compression, a larger compressed import, Read/Write-disabled original reading, current working color/UV2 preservation, temporary-asset cleanup, budget backoff, full metrics for accepted candidates and monotonic candidate reuse. A focused two-level source-retention reporting regression also passes. Both licence-free C# compile configurations pass. This does not mark the complete package test suite or all visual requirements as passed.

Automatic small-part deletion, corrected normal/color transfer, practical reduction of irregular source loops, thin-feature guards and original URP material/distance evaluation remain unfinished.

## Follow-up: relaxed final-level quality

LOD Gen now defaults to a progressive far profile beginning at LOD2. Earlier levels keep their settings. With two generated levels the final profile uses Target Error 0.30, Normal Weight 0.5, Color Weight 0.25, sampled maximum RGBA error 0.10, and a 30-degree normal-angle guard for loop/hybrid mode. The UI exposes the starting level, endpoint settings and a preview of each effective profile. The toggle restores uniform base settings; other pipeline callers without a per-level profile are unchanged.

The follow-up in `_results~/lod-far-20261009/` compares original meshes, strict LOD2 and relaxed LOD2 on all eight project models. Original FBX hashes, authored colors and model selection remain unchanged. The tent uses the same 90-degree X rotation as the previous follow-up. Both profiles target 25% of LOD0; the relaxed path keeps the exact strict LOD1 settings.

| Mesh | Strict LOD2 tris | Relaxed LOD2 tris | Relaxed sampled RGBA maximum | Strict silhouette mismatch | Relaxed silhouette mismatch |
| --- | ---: | ---: | ---: | ---: | ---: |
| Park Bench B, authored RGBA | 9334 | 8950 | 0.09124 | 0% | 0% |
| Rainbow tent, authored RGBA | 528 | 524 | 0.02415 | 0% | 0.16% |
| Wrench | 153 | 152 | N/A | 20.60% | 22.36% |
| First Aid Kit | 720 | 720 | N/A | 2.57% | 2.46% |
| M84 grenade | 506 | 496 | N/A | 8.22% | 7.90% |
| Park Bench A | 958 | 958 | constant color retained | 4.99% | 5.51% |
| Fire Shield | 530 | 494 | N/A | 32.97% | 39.93% |
| Rock | 252 | 252 | constant color retained | 4.13% | 3.87% |

Silhouette values are the worse of the two diagnostic views. These are trade-offs, not a guarantee of improved quality: Fire Shield loses more silhouette while saving 36 triangles. The colored bench finally reduces by 4.1% of its source count, while the tent's additional saving is only four triangles. Neither colored asset reaches the requested 25% budget. Source colors remain present; the finite per-level cap is enforced against sampled LOD0 correspondence, including alpha.

Maximum pixel RGBA differences on the relaxed bench and tent are 0.04150 and 0.00830 in these two views. Screen-space differences also contain changed projection and partial pixel coverage; a constant field can have a screen difference without a changed vertex field. The surface cap therefore does not promise the same maximum in a rendered image. Original URP material and distance-transition testing are still outside this capture harness.

Verification: the actual-project GPU fixture passes with all eight cases in `far-v3.xml`. A strict-mode regression needed to explicitly disable the new default relaxation; after that adjustment, `unit-final.xml` contains 58 passing tests with no failures or skips, covering all three reduction modes, bounded alpha loss, unchanged near limits, actual LOD numbering, invalid-profile rejection before mutation and the UI toggle. The four procedural GPU tests also pass. Both C# FBX configurations compile without errors and meta/diff checks pass. The unrelated full package suite was not rerun.

The first attempt exhausted local disk space while writing raw captures, so old raw `.rgba` captures from this task were removed while keeping their PNGs, metrics and reports. The latest complete raw captures are in the far-profile directory; the earlier PNG comparisons and recorded metrics remain available.

## Reproduction and artifacts

Run the Editor fixture `SashaRX.UnityMeshLab.Tests.LodProjectVisualQualityTests` with a GPU (omit `-nographics`), passing `-meshlabLodProjectCases <dataset.json>` and `-meshlabLodVisualOutput <output-directory>`.

The opt-in dataset has `cases`, each containing `name`, copied `asset` under `Assets/LodProjectCopies/`, `sourcePath`, `sourceSha256`, optional `albedo` asset path and `rotation` with x/y/z. Copy the FBX and its importer metadata before the run. Without the dataset argument the fixture skips instead of depending on this workstation's asset paths.

```text
python Tools~/render_lod_project_quality.py <output-directory>
```

Initial PNG images, source provenance, per-mesh JSON metrics and the detailed report are in `_results~/lod-project-20261009/`, which is ignored and excluded from the Unity package. The latest PNGs, raw float captures and per-level quality metrics are in `_results~/lod-far-20261009/`. Its `project-overview.png` compares original meshes with strict and relaxed LOD2; compact/full sheets include wireframes and error maps. Perspective distance transitions, normal-map appearance, original URP materials, animated poses and complete assemblies remain outside this evaluation.
