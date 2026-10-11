# Forward and Reverse UV: paired model comparison, 2026-10-11

## Authored-chart default seed

Baseline `81e9fb59`, fresh full-pipeline artifacts:
`_results~/source-seed-default-20261011/paired/`. Reverse starts from detached
original coarsest meshes, retaining UV0 chart domains, applying the ordinary
Repack ARAP metric repair, then seed normalization and local intrinsic rescue.
No prior forward Transfer is required. Unity seed charting is now opt-in.

All six Reverse chains complete at 32 tex/m and padding 2. All LOD quality
reports contain zero collapsed UV faces and unexpected overlap pairs; original
input snapshots remain unchanged. Thirty ordinary Forward mesh layout exports
are byte-identical to the preceding comparison. LOD0 measurements:

| Model | Exclusive bbox fill / P95 stretch | Overlap bbox fill / P95 stretch | Allowed overlap pairs |
|---|---:|---:|---:|
| Cafe_Table_A_01 | 52.7% / 1.000 | 55.4% / 1.000 | 38 |
| Modern_Kitchen_A | 36.4% / 1.072 | 40.5% / 1.055 | 683 |
| Kamaz Typhoon | 53.3% / 1.209 | 43.9% / 1.223 | 3531 |

This changes the seed policy, not a claim of uniformly improved packing.
Kitchen and Kamaz overlap retain the audited seed trial without optional
junction cuts after the cut chain introduces an unclassified contact. Cafe
overlap retains baseline donor faces after a contact refusal; both Kamaz modes
retain baseline donor faces where a seam trial loses inheritance. Final family
packing completes in all six modes. These refusals remain in the report and
never trigger Unity seed charting automatically. Finer unsupported surface
charting remains a separate path.

The final graphics-enabled EditMode run passes 331 cases with 34 optional skips
and zero failures. Both FBX reference-compile variants pass. A preliminary
no-graphics run had two lightmap texture-readback failures; both pass with the
graphics device active. Source seam/material/tangent/colour preservation and
the explicit Unity option are covered by new controls. Visuals show actual
last-LOD and LOD0 UV exports for ordinary Forward and both Reverse policies.

## Current continuation: connected families and independent packing searches

Latest fresh paired artifacts: `_results~/finalpack-20261011/regression/paired/`.
Baseline is `a51de228`; the older texel-policy captures below have identical
input hashes and transforms. All exported positions, indices, metric frames
and face correspondence records also match. This isolates the result to final
UV placement, rather than accepting a change of geometry or donor selection.

LOD0 bbox fill at 32 tex/m, padding 2:

| Model | Forward | Reverse exclusive before / after | Reverse overlap before / after |
|---|---:|---:|---:|
| Cafe_Table_A_01 | 40.2% | 59.5% / 59.5% | 61.5% / 61.5% |
| Modern_Kitchen_A | 39.4% | 40.9% / 40.9% | 45.6% / 45.6% |
| Kamaz Typhoon | 57.5% | 34.1% / 52.6% | 50.1% / 50.1% |

Kamaz exclusive at 10 tex/m improves from 10.0% to 38.9%; overlap stays at
40.4%. Atlas sizes do not change: Kamaz 512/1024, Cafe 64/128 and Kitchen 256
at 32 tex/m. All completed Reverse chains retain zero collapsed faces and
unexpected overlap pairs. Original mesh snapshots remain unchanged.

The old final pack treated a retained region label as a rigid family even
after its disconnected pieces had been placed separately. It reserved the
empty space between those pieces. Packing now starts with geometric/UV
corner-connected components and joins their recorded donor/overlay links.
Preliminary tightening no longer pre-exhausts every fresh footprint order.

The affected Unity run recorded 380 tests: 372 passed, 7 optional skips and
one fixture failure. The fixture marked faces inherited without recording
their parent LOD/face, relying on coincident chart numbers to join surfaces.
It now supplies explicit ancestry and additionally asserts one shared family;
the original UV, density, geometry and overlay assertions remain.
The final-source focused repeat passes 206 tests with zero failures and five
optional skips, including the corrected fixture and all six packing replays.
Both FBX reference-compilation variants pass. These are separate runs, not one
fresh all-green 380-test run.

Six separate completed-chain replays audit every recorded ancestry and ordered
overlay relation against one quarter-turn, scale and translation. The largest
motion error is below 0.00014 pixel. These replays start from already packed
outputs and can choose another atlas size/density; their additional fill gains
are not substituted for the fresh pipeline measurements above.

The nine-asset inherited-area regression gate retains its frozen thresholds
in both policies: eight assets complete, Kitchen retains its existing seed
refusal at the corpus density. The current fix does not resolve Kitchen's
packing, excess Kamaz fragmentation, ordinary Forward target defects or the
previous semantic overlap-coordinate witnesses described below.

## Historical continuation: source-local metrics and family compaction

Paired artifacts: `_results~/reverse-model-repair-20261011/texel-policy/paired/`.
These supersede the older paired2 measurements below. Both runs use the same
frozen inputs, settings and source-preservation checks. The comparison PNGs
contain every LOD, with Forward, Reverse exclusive and Reverse overlap columns.

LOD0 bbox fill at 32 tex/m:

| Model | Forward | Reverse exclusive | Reverse overlap |
|---|---:|---:|---:|
| Cafe_Table_A_01 | 40.2% | 59.5% | 61.5% |
| Modern_Kitchen_A | 39.4% | 40.9% | 45.6% |
| Kamaz Typhoon | 57.5% | 34.1% | 50.1% |

Kitchen Reverse now completes both policies at 32 tex/m with zero collapsed
faces and unexpected overlap pairs on both LODs. Its intermediate 11.5/11.6%
fills improved through validated family compaction; P95 stretch is 1.07 on
LOD0 and approximately 1.00 on LOD1. At 10 tex/m it still refuses a positive-area
seed face whose pixel altitude is about 1.43e-8. That face is preserved, and the
refusal remains visible rather than publishing an incomplete mesh.

Kamaz Reverse has zero collapsed faces and unexpected overlap pairs across all
three LODs. Exclusive fills are 34.1/41.2/43.0%; overlap fills are 50.1/49.9/44.3%.
LOD0 P95 stretch is 1.15/1.16, and maximum stretch remains below 4. However,
exclusive LOD0 packing is worse than paired2, and drops to 10.0% at 10 tex/m.
It has 5262 connected charts at 32 tex/m versus 2146 in Forward. Its optional
junction refinement still refuses a face that loses inheritance across a seam.
This is an unresolved fragmentation/correspondence defect, not a satisfactory
packing result. Overlap mode at 10 tex/m fills 40.4%.

Ordinary Forward is also unresolved: Kitchen LOD0 has 54 collapsed UV faces;
its LOD1 has 13 and 326 unexpected pairs. Kamaz LOD1 has 111 collapsed faces
and 594 unexpected pairs; LOD2 has 84 and 140 in the paired capture. Dense
Forward source packing cannot establish valid transferred targets.

The complete affected run recorded 637 tests: 632 passed, 4 skipped, one failed
on the Kamaz Forward pair-count cap. Exact comparison with the frozen input
showed the same overlap area (0.000226321486 versus 0.000226321546), while seam
subdivision increased 3786 faces to 4462 and 96 intersecting pairs to 152. The
regression gate now retains the frozen overlap-area baseline with 1% tolerance.
A separate control verifies that subdividing both intersecting faces increases
pair count while preserving their overlap-area sum. The subsequent focused
Forward/viewport/quality run passed 206 tests, skipped 8 optional tests, and
had no failures. Both FBX compilation variants passed. This is a full affected
run plus a focused repeat, not a claim of one fresh all-green 637-test run.

The nine-asset inherited-area gate passed in both overlap policies. Eight
assets completed; Kitchen at its corpus density retained its existing seed
refusal. DressingTable recovered to 87.97/87.98% inherited area, above the
unchanged frozen acceptance threshold. Increasing maximum atlas capacity no
longer rejects representable cuts in the actual working donor atlas. Original
mesh snapshots were preserved throughout.

Independent overlap-coordinate spot checks are also retained. Kitchen tests
500 of 520 marked faces with maximum error 0.079 texel. Cafe tests all 48, below
0.000007 texel. Kamaz at 32 tex/m tests 2009 of 2418 faces; twelve tested corners
exceed 0.25 texel, with maximum 0.831 texel. This audit uses each face's recorded
immediate donor triangle, so semantic donor selection and other corners remain
unproven. The witnesses must be traced before declaring overlay correspondence
correct for every detail.

Latest main `cd8d848d` (package 1.1.42) is integrated after those UV captures.
It adds shared FBX format/embedding settings. Import probes retain their source
policy, while internal polygon splitting still uses a Binary scratch document.
All four Binary/ASCII input/output combinations retain audited UV seams and
neighboring polygons. Same-format stored doubles remain exact; SDK Binary-to-ASCII
conversion is allowed one double spacing in the stored-channel test. The source
FBX and metadata byte-preservation checks remain exact. The post-merge FBX run
passes 217 tests with zero failures and one optional skip; both reference compile
variants also pass. It does not constitute a repeated UV-model capture.

## Historical paired2 baseline

This compares the ordinary LOD0-to-coarse workflow against progressive
coarse-to-LOD0 Reverse, including both projected-overlap policies. The result
does not establish that either workflow is ready for arbitrary models.

Baseline: `eccca7d1439c7701b5a03790a763f3b140896b1b`, plus the unpublished
underlay/strip/footprint changes in this worktree. These are dirty-worktree
results, not measurements of a published package revision.

## Method

`PreviewLodSwitchTests.FrozenForwardReverseComparison` uses private copies of
Cafe_Table_A_01, Modern_Kitchen_A and Kamaz Typhoon (`kamaz_Off.fbx`). Each model
is run at 10 and 32 tex/m, padding 2, border padding 0, shared atlas, rotation
and axis alignment enabled, narrow-junction cuts enabled. Forward uses adaptive
symmetry and the normal full pipeline. Reverse prepares its seed independently;
it never consumes the Forward output. Both start from the same frozen imported
mesh data and renderer transforms after precision-preserving input preparation.

Each capture records input hashes, transforms, requested settings, completion or
refusal, UV/geometry for every LOD and the same `LightmapUvQuality` measurement
used by the Viewer. Mesh snapshots are compared after all runs. All six paired
captures preserved their source meshes.

Packing fill is measured inside each layout's **own bbox**, not inside a forced
unit square. An intentionally shorter rectangular layout is not penalized for
the unused square outside its bbox. The native Forward pack dimensions include
internal oversampling; they are not compared as final bake resolutions. The
extra analysis reports normalized UV density without confusing these internal
pixels with the final lightmap resolution.

Unity 6000.2.6f2, DX11, separate disposable consumer project; the user's open
Unity projects and installed package were not updated. Local artifacts:
`_results~/forward-versus-reverse-20261011/paired2/`. The six capture tests passed
in 236 seconds. This means capture and source-preservation checks passed;
algorithmic refusals are recorded below, not converted into successful layouts.

## LOD0 packing at 32 tex/m

| Model | Ordinary Transfer | Reverse exclusive | Reverse overlap |
|---|---:|---:|---:|
| Cafe_Table_A_01 | 40.2% | 59.5% | 61.5% |
| Kamaz Typhoon | 57.5% | 46.4% | 49.4% |
| Modern_Kitchen_A | 39.4% | seed refused | seed refused |

Cafe has no collapsed UV faces or unexpected intersections in either direction.
Its area-weighted P95 anisotropy is 1.21 on Forward LOD0/1 and 2.10 on LOD2;
Reverse is approximately 1.00 on all three. Overlap mode records 38 allowed
triangle-intersection pairs on LOD0 and LOD1. The successful table result must
not stand in for the other models.

## Kamaz at 32 tex/m

| Mode | LOD | bbox fill | P95 anisotropy | collapsed UV faces | unexpected overlap pairs |
|---|---:|---:|---:|---:|---:|
| Forward | 0 | 57.5% | 1.06 | 0 | 0 |
| Forward | 1 | 56.4% | 1.10 | 114 | 594 |
| Forward | 2 | 46.0% | 1.21 | 87 | 95 |
| Reverse exclusive | 0 | 46.4% | 1.15 | 0 | 0 |
| Reverse exclusive | 1 | 45.8% | 1.14 | 0 | 0 |
| Reverse exclusive | 2 | 39.1% | 1.07 | 0 | 0 |
| Reverse overlap | 0 | 49.4% | 1.16 | 0 | 0 |
| Reverse overlap | 1 | 49.0% | 1.16 | 0 | 0 |
| Reverse overlap | 2 | 43.3% | 1.07 | 0 | 0 |

Reverse overlap additionally records 3779 allowed pairs on LOD0 and 1289 on
LOD1. P95 does not expose all thin-face failures: the Forward target also has
extremely stretched individual triangles. A dense source atlas is therefore
not evidence of a valid transferred atlas.

LOD0 has 2146 connected UV charts in Forward versus 4486/4617 in Reverse
exclusive/overlap. This count uses exact shared UV edges in the exported output;
it is different from a provenance family count. Reverse produces more small
independent regions and leaves a large empty block within its bbox. All four
Kamaz Reverse trials at 10/32 retain rectangle family packing:
`finalPackingFootprints=false`. Their optional seed preparation reports a
Float32 metric refusal; accepted donor seam cuts also disappear
(`splitSourceFaces=0`). Successful analytic letter-shape controls do not resolve
these actual model refusals.

At 10 tex/m, Kamaz LOD0 bbox fills are 24.1% Forward, 39.5% Reverse exclusive,
and 37.4% Reverse overlap. Consequently, no unconditional claim that Reverse
packs better or worse is supported: compare settings and the complete chain.

## Kitchen and remaining regression

Kitchen Reverse refuses seed preparation in all four runs, before publishing a
layout. Face 53 cannot preserve its UV metric at final Float32 placement,
including the audited fallback. Forward completes but is defective: at 32
tex/m it contains 54 collapsed LOD0 UV faces, and LOD1 has 12 collapsed faces
and 326 unexpected intersection pairs. At 10 tex/m these are 55, 4 and 251.

The separate 621-test affected-suite run had 616 passed, one failed and four
skipped. The failure is a real inherited-area regression on
Modern_DressingTable_A in both overlap policies. The unpublished Reverse rewrite
does not pass its frozen corpus acceptance gate.

## Overlay-coordinate spot audit

An additional independent calculation projects output corners onto their
recorded immediate donor triangle and compares its barycentric UV against the
exported UV. Only projections inside that donor triangle are included; a face
can legitimately use another triangle of the same chart. This is a spot audit,
not a complete proof of semantic underlay selection.

Cafe tests all 48 marked overlay faces, with maximum error below 0.000007 texel.
At 32 tex/m, Kamaz tests corners on 2013 of 2422 overlay faces. Seven tested
corners differ by more than 0.25 texel; the maximum is 0.653 texel. Witness mesh,
face, corner and donor coordinates are saved in
`comparison/overlay-coordinates.json`. These require tracing the actual selected
donor for each corner before concluding whether projection or the single-face
provenance record is responsible. Allowed overlap counts alone do not prove
that every detail was placed on the intended underlay.

## What the comparison identifies

1. Seed normalization, seam subdivision and new-chart allocation need to retain
   complete valid regions on real models. Unconditionally accepting the layout
   of a simple control is insufficient; Kamaz still rolls back its cuts, and
   Kitchen has no Reverse result.
2. A projected detail and its underlay need verified coordinate correspondence
   and one shared packing transform. A boolean allowed-overlap classification
   cannot substitute for that coordinate check.
3. Final family packing must use the actual occupied union. Its current optional
   footprint attempt is not accepted on Kamaz, so rectangular reservations keep
   the holes and gaps visible in the comparison.
4. Ordinary Forward needs separate target-face validation and repair. Its dense
   LOD0 packing hides substantial target collapse and overlap defects.

Regressions and refusals remain visible in the benchmark. The current rewrite
is not described as a completed Transfer fix.
