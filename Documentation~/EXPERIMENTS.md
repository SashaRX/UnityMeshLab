# Transfer Pipeline — Experiments & Lessons

## Partitions facing the same way are told apart by position — 2026-10-22

Raised by the Codex review of PR #329. `SpatialPartitioner.MatchPartition`
picks a source partition for each target vertex by normal alone whenever the
partitions have normals, which every successful split has (flood-fill
components and the normal, UV0-winding and position splits). Partitions facing
the same way tie on the normal and the first one wins wherever the vertex lies:
a position split, parallel panels, the halves of a mirrored layout, or a fold
that flips the UV0 winding but leaves the surface facing up. The merged path
then restricts its UV0 and 3D projections to that partition, so the vertex can
take the other layer's UV2. That path also skipped the exact source-vertex
lookup the interpolation path uses.

Among partitions whose average normals lie within 15° of the best match, the
nearest 3D centroid now decides, the position split's own assignment rule.
Partitions facing apart (the front and back of a strap) are still chosen by
normal. A constrained merged pass keeps the UV2 of a retained source vertex
(same position and UV0, with one UV2) as interpolation does; an all-source pass
does not, since it looks for UV2 space outside the chosen source on purpose.

Measurement pending: the furniture corpus against `main-5e9f9c0` with
`report.py --baseline … --require-no-regressions`, and model A with PR #329.

## Rejected merged shells: write a safe candidate instead of (0,0) — rejected — 2026-10-21

`GroupedShellTransfer` rejects a merged target shell when more than half of its
faces have issues (missing UV2, collapsed UV faces, or stretch above 10,000:1)
and it is not a force3D shell. The shell's UV2 is not written: its vertices stay
at (0,0) and every face collapses onto the atlas corner. Rejected shells keep
the "merged" method, so the merged-shell AABB safety net still visits them; when
the origin lies outside the source's allowance and the source has a UV0 BVH
(more than 16 faces), the net re-projects the whole shell vertex by vertex
without a quality check, while the shell stays `Rejected`. In the corpus control
log 24 of the 4587 `REJECTED(` lines (counted over every repeat of a mesh) are
followed by such a `Post-fix:` re-projection.

Tried (PR #305): write the shell's best merged candidate instead, when it maps
every vertex to a finite point, has fewer issues than the shell has faces, has
no positive-area self-overlap and lies within the safety net's allowance around
its source. The same gate was applied again at classification, withdrawing a
fallback that no longer passed; the PR's last revision also applies it before
final recovery, which the measured revision did not.

Measured on the furniture corpus (89 files, 256 LOD pairs) together with PR #308
against main, `report.py --baseline … --require-no-regressions`: failed. Grouped
transfer has 4 / 3 / 4 tradeoff pairs (default / no-reparameterization /
oversample-8), no worse or invalid rows; source packing and the comparison
methods are unchanged. The log shows 47 fallback writes and no withdrawal after
the final checks. In the changed pairs collapsed UV faces and missing UV2 fall
(3083 → 3073; 12 → 10 with misses 12 → 0), but overlap pairs grow in every one
(780 → 807, 72 → 80, 3 → 7), and worst anisotropy jumps where the corner layout
had none (3.27 → 4.6e18). PR #308 was in the same run; its own run is pending,
and these shifts follow the fallback's mechanism. The earlier withdrawal checks
the same gate, which lets slivers and overlaps with other shells through, so it
would not remove them.

Why it fails: the candidate unfolds part of the shell, and the faces it cannot
map become near-zero slivers instead of points at the corner, which the quality
metric scores as extreme anisotropy rather than as collapsed. The gate checked
self-overlap only, so the unfolded shell lands on neighbouring charts. Requiring
no slivers and no new overlap with other shells leaves practically no candidate.

Do not repeat: writing a rejected merged shell's candidate gated by issue count
and self-overlap. A future attempt needs an overlap check against the other
written shells and a bound on sliver anisotropy, and must beat the corner
layout in the corpus.

## Merged-shell safety net: small sources — rejected — 2026-10-21

After Phase 3 writes the merged shells, a safety net re-projects every merged
vertex outside its source's UV2 bounds widened by 20% of their size: it finds
the nearest source face in UV0 and takes that face's UV2. The search goes only
through the source shell's UV0 BVH, built for shells of more than 16 faces
(`kMinFacesForShellBvh`), so outliers of a smaller source stay where they are.

Tried (PR #310): without a BVH, scan the source's faces with the BVH's rule
(faces whose normal agrees first, then any face), skipping `Rejected` shells.

Measured on the furniture corpus (89 files, 256 LOD pairs) against main,
`report.py --baseline … --require-no-regressions`: failed. Grouped transfer:
default 247 same / 1 better / 5 tradeoff / 3 worse, no-reparameterization
247 / 0 / 9 / 0, oversample-8 248 / 0 / 7 / 1; other methods unchanged. Of the
26 changed pairs, overlap pairs fall in 6 default pairs (350 → 267, 30 → 19),
but collapsed UV faces rise in 4 / 5 / 4 pairs per profile and fall in none, and
worst anisotropy rises in 5 / 4 / 4 and falls in none (291 → 5.9e17). The log
has 162 `Post-fix:` lines against 51 on main (53 shell totals against 22).

Why it fails: the net moves each outlier vertex on its own, while the other
vertices of its faces stay, so a face with a moved vertex becomes a sliver or
folds. A small source's allowance is small in absolute terms, so far more of
its vertices count as outliers than for a large source. The same per-vertex
move runs on main through the BVH; it is not measured separately.

Do not repeat: extending the per-vertex re-projection to more shells. The
follow-up treats a shell as one unit: re-project its outliers and keep the
result only when the shell's collapsed faces and worst anisotropy do not grow.

## Merged shells record the source their UV2 came from — 2026-10-21

In Phase 3 of `GroupedShellTransfer`, a merged target shell in a source overlap
group first tries a composite of the group's members. When the composite has
too many issues, the shell falls through to the ordinary merged path with
`chosenSrc = bestOverlapSrc`. The written UV2 and `vertexToSourceShell` then
come from that source, but `targetShellToSourceShell[tsi]` kept the source
matched before the overlap analysis. Four later readers used the stale value:
the merged-shell AABB safety net (bounds and UV0 BVH for outlier
re-projection), `PlanarTransferRecovery` (projection source), classification,
and the next LOD's overlap and match hints.

The UV2 write now records `chosenSrc` as the shell's source. `chosenSrc` changes
only at that fall-through, so for every other shell the assignment is a no-op;
a rejected shell writes no UV2 and keeps its earlier source. PR #305's fallback
for rejected merged shells already records its candidate's source and restores
the earlier one when the fallback is withdrawn, which stays consistent with
this change.

This changes outputs for merged shells that took the fall-through to a
different source, so it is gated by the corpus rather than by bitwise
comparison: a standard capture against main, compared with
`report.py --baseline ... --require-no-regressions` (no worse, tradeoff or
invalid rows). No synthetic fixture reproduces the fall-through yet.

Result: measured together with the safety-net change below (main `408e16e`); see
that entry. The combined gate passed.

## Merged-shell safety net: judge a shell's re-projection as one change — 2026-10-21

After Phase 3, the safety net re-projects every merged vertex outside its
source's UV2 bounds widened by 20% of their size, through the source's UV0 BVH,
vertex by vertex: the rest of a moved vertex's faces stay, so a face can become a
sliver or fold. Extending this to sources too small for a BVH (PR #310) failed the
furniture corpus: overlaps fell in 6 pairs, but collapsed UV faces and anisotropy
only rose (4 worse pairs, the rest tradeoffs; recorded in PR #316).

Now a shell's re-projection is one change, scored by the corpus metrics on that
shell (`ShellLayoutScore`): invalid, degenerate and exactly zero-area UV faces,
out-of-bounds vertices, worst anisotropy, the whole mesh's area-weighted
anisotropy, and the overlap pairs and area of the shell with itself and with
every other face whose UV2 bounds meet it before or after. One layout dominates
another when it is no worse in any of these and better in one, by the report's
own rule (integers exactly, real values within a relative 1e-6). Main's behavior
is the default, and only domination changes it:

- through a BVH (main re-projects), the transferred layout is kept when it
  dominates the re-projection;
- for a source of 16 faces or fewer (main leaves the outliers), the source's
  faces are scanned as in PR #310, and the re-projection is applied when it
  dominates the transferred layout.

So each shell's result is no worse than main's by the corpus metrics measured at
this stage; later stages (overlap report, recovery, classification) may still
react to a different layout. A rejected shell at (0,0) keeps main's result: its
corner layout cannot dominate a re-projection and a re-projection with any finite
stretch cannot dominate it. `ShellLayoutScoreTests` covers the domination rule;
`SafetyNetScanOfASmallSourceFindsTheFaceItsBvhWould` the scan.

Result (main `408e16e`, which carries this change and the merged-shell source
record above, against a control on `7aaecf9`): accepted. Both captures ran on a
frozen copy of the corpus, because 13 of its 89 files had changed since the
previous control; 256 LOD pairs, 2304 measured rows each, no input hash differs.
`report.py --baseline … --require-no-regressions` exits 0: 2302 rows same and 2
better (one LOD1 target, default and oversample-8: collapsed UV faces 13 → 12,
overlap pairs 151 → 149 and 157 → 155, area-weighted anisotropy lower), no
worse, tradeoff or invalid rows. `Post-fix:` lines 56 → 54; `Post-fix declined:`
0 → 3, all one shell in three repeats keeping its transferred layout over
re-projecting 4 outlier vertices. Rejected shells 2966 in both, 29 of them with
a `Post-fix` line in both.

## Auto-tune: stretch and out-of-bounds vertices are defects — 2026-10-21

Item 1 of `TRANSFER_METRICS_AUDIT.md`. `AutoTuneScore` (PR #297) ranks attempts
by failed targets, incomplete scans, collapsed faces, rejected shells, overlap
area, force3D detections and coverage, and stops the search at an attempt with
none of these defects. It had no stretch or bounds term. An attempt whose final
`TransferUvQuality` still had a face at 50:1, or a vertex outside [0,1] when
the publication clamp is off, could be called perfect and end the search,
although the workflow itself treats worst anisotropy above 4 as a reason for
the donor-seam trial.

The score now also keeps the worst anisotropy over all targets (rendered frame,
as measured) and the summed out-of-bounds vertices. `Perfect` additionally
requires zero out-of-bounds vertices and a worst anisotropy of at most 4
(`AutoTuneScore.PerfectAnisotropy`, the trial threshold). When all earlier
criteria tie, coverage included, fewer out-of-bounds vertices win, then a worst
anisotropy lower by more than 1%. The earlier criteria keep their order, so an
attempt can only win on stretch where it was tied before.

Cost: a model whose every attempt keeps a face above 4:1 no longer stops after
the first attempt and runs all separations; PR #295 still reuses attempts whose
symmetry split repeats. Auto-tune can now pick a different separation only
among attempts that tied on every earlier criterion.

`AutoTuneScoreTests` cover the perfect condition, the worst-target aggregate
and the tie order. The furniture corpus does not run auto-tune, so validation
is the paired benchmark: `run-transfer-performance.ps1 -Repetitions 1
-Density 32` against main, reporting per model the attempts run, the chosen
configuration and the Forward pipeline time.

Result (paired benchmark, main `408e16e` with this change and the donor-seam
trigger below against `0228f46`, 3 repetitions at 32 tex/m on Cafe_Table_A_01,
Modern_Kitchen_A and kamaz_Off): accepted. All 120 exports are byte-identical.
Every model still selects configuration #0. Cafe now runs 3 attempts instead of
1: its #0 has no collapsed face, rejected shell or overlap but a worst
anisotropy of 2047, so it is no longer perfect; attempts #1 and #2 repeat its
symmetry split and are reused (3 ms). Kitchen and Kamaz ran 3 attempts before
and after. Forward pipeline medians 0.420 → 0.428, 3.424 → 3.437 and
16.435 → 16.425 s: noise.

## Donor-seam trial: zero-area geometry alone does not start it — 2026-10-21

Item 7 of `TRANSFER_METRICS_AUDIT.md`, the zero-area part of F5. After shell
transfer the workflow starts the donor-seam trial when the target's
`TransferUvQuality` has worst anisotropy above 4, an overlap pair, or any
degenerate face. `TransferUvQuality` counts a face as degenerate when its 3D
frame has zero length or area, before it looks at the UVs. A target with one
zero-area 3D face therefore ran the whole trial (the most expensive Forward
stage; 12 of 21 s on Kamaz) even with a perfect layout. Seam preparation
removes exactly zero-area faces, so such a trial could even be accepted for
the deletion alone.

The trigger (`UvTransferWorkflow.NeedsDonorSeamTrial`) now subtracts the
zero-area 3D faces counted by `AutoTuneScore.GeometryDegenerateFaces`, the same
predicate in the same rendered frame that auto-tune already uses. Stretch,
overlap and collapsed UV faces start the trial as before; a degenerate count
above the zero-area geometry still does.

This changes outputs for targets whose only defect was zero-area geometry: they
keep the shell-transfer layout and their faces instead of a seam-prepared mesh.
`DonorSeamTrialTriggerTests` cover the predicate. The furniture corpus does
not run the donor-seam trial, so validation is the paired benchmark
(`run-transfer-performance.ps1 -Repetitions 3 -Density 32` against main):
which exports differ, and the "Forward donor seam trial" samples and median
per model.

Result (the same paired benchmark as the auto-tune entry above): no change on
these models. Exports are byte-identical and the trial ran as often as before
(1, 4 and 6 samples; medians 0.130 → 0.136, 1.447 → 1.495 and 10.931 → 11.093
s, noise): none of their targets had zero-area geometry as its only defect.
The change stays as a correctness fix of the trigger.

## Oversample 8: why a better source does not survive transfer — analysis — 2026-10-21

The furniture corpus baseline on main (`491340a`, 80 files, 255 LOD pairs)
compares each profile with `default`. `oversample-8` improves the packed source
(137 better, 17 worse, 101 tradeoff), but its grouped transfer is mixed
(65 better, 61 worse, 129 tradeoff). In the corpus, `default` and `oversample-8`
pack the same welded, pre-optimized source at 512 px with density normalization
and reparameterization; for the square texture metric the aspect preparation is
the identity. The requested internal oversample (4 against 8) is the only pack
setting that differs.

Findings from the code:

- `XatlasRepack.RepackSingle` returns xatlas's own atlas size, which is the
  internal oversampled raster (about requested × oversample), not the requested
  resolution. `NormalizePackedLightmap` keeps it as the square side.
- The corpus passes that side to `GroupedShellTransfer.Transfer`, and so does the
  workflow: `repackedAtlasWidth` is the same side, used by Forward transfer, the
  donor-seam trial resolution and Reverse's seed resolution.
- Grouped transfer derives its UV2 tolerances from that side:
  `ComputeUv2PixelMargin(side, 1.25 px)` for out-of-bounds margins and `2.5 px`
  for bounds (transform and candidate rejection), and the planar-recovery band in
  `RecoverFinalProjection`. When the packs reach oversample 8 and 4, each is half as
  wide in UV space at 8 as at 4, and both are 4–8× narrower than a texel of the
  512 px lightmap.
- The pack budget may lower the requested oversample (`TryResolveInternalPackDimensions`);
  the corpus row does not record the reached factor, but `atlasSide / resolution`
  shows it.

Hypotheses, each with the measurement that settles it:

1. **Transfer side (H1).** The mixed transfer comes mostly from the narrower
   tolerances, not from the packed layout. Settled by
   `grouped-at-resolution` (transfer at the requested 512 px): if oversample 8
   against 4 becomes mostly better at the same side, H1 holds. The side change
   alone (requested vs internal, at oversample 4 and at 8) also shows whether the
   production default (internal side at oversample 4) costs quality.
2. **Source-atlas gate (H2).** A cleaner source opens the source-atlas recovery gate
   (`GroupedShellTransfer` recovery is disabled for a source with overlaps,
   collapsed faces or out-of-bounds UV2). Recovery then runs where it did not, which
   can trade one metric for another. A `better` source verdict does not open the
   gate by itself: the source may still keep a blocking defect. Settled by the traced
   gate table: pairs whose gate goes from closed to open with a `tradeoff` or `worse`
   transfer point here, and more clean-chart recoveries confirm it. The count misses
   merged and overlap-group candidate selection, which the gate also enables, so an
   unchanged count does not rule H2 out. The retention table only shows whether the
   source gain survives.
3. **Reached oversample (H3).** Some packs do not reach 8. Settled by the reached
   oversample distribution.
4. **Metric noise (H4).** Many tradeoffs differ only in the area-weighted or worst
   anisotropy within a few percent while defect counts are equal. Settled by the
   per-metric table: if defects (`degenerateFaces`, `overlapPairs`,
   `uvDegenerateFaces`) are mostly `same`, the mixed verdicts are stretch-only.

Tooling: `FurnitureCorpusTests` adds the `grouped-at-resolution` rows under
`MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS=1` and traces grouped transfer there
(`sourceAtlasRecoveryAllowed`, `cleanChartRecoveries`); standard captures run untraced;
`Tools~/FurnitureCorpus/oversample.py` writes the breakdown; `report.py` now
compares each profile with the default row of the same method and shares its
per-metric verdicts with the analysis.

Measurement pending: one corpus capture with the control on main, then
`oversample.py`. No production change follows until the numbers settle H1–H4;
if H1 holds, the candidate change is to pass the requested resolution (or the
internal side divided by the reached oversample) to transfer and recovery.

## Atlas overlap scan: triangles by reference — 2026-10-21

`UvAtlasDiagnostics.Measure` is the overlap scan behind every transfer quality
measure, contact audit, envelope audit and packing validation. Its `Triangle`
is a 96-byte struct (face, chart, three double points, bounds, area) held in a
`List<Triangle>`. In the pair loop each charged pair copied `triangles[j]`, and
the predicates took both triangles by value: `Separated` copied them once, each
of its six `SeparatedBy` calls again, and each `Project` once more, so a
candidate pair copied about 2.5 KB before any arithmetic; `IntersectionArea`,
`Clip` and `Cross` also copied their points.

The triangles now live in an array read through `ref readonly` locals, and the
predicates take `in` parameters. The arithmetic and its order are unchanged, so
reports are identical bit for bit.

A standalone .NET 8 harness (Unity types shimmed) compared the previous and
the new `Measure` on 72 configurations: 60² and 150² cell grids at jitter 0,
0.6 and 2.5 cells (from a clean packed grid to heavy overlap), two seeds,
budgets unbounded / 2,000,000 / 5,000, with and without `crossChartOnly`. Every
report field matched, including the bits of `pairAreaSum`, the samples and the
sorted conflicts. Scan time (two timed repetitions each, grid construction
included): 3,698 → 2,827 ms (−24 %). Unity's Mono JIT handles large struct
copies no better than RyuJIT, so the editor gain is expected to be at least
as large.

Measurement pending: `Tools~/run-transfer-performance.ps1 -Repetitions 3
-Density 32`, control = main before this change, with
`compare-transfer-performance.py` confirming all exports are byte-identical.
The stages that run the scan include "Forward quality measure", "Forward seam
quality measures", "Forward seam contact audits" and "Native envelope audit".

## Forward donor-seam trial: measure each layout once — 2026-10-21

A Forward target whose shell transfer leaves overlaps, collapsed faces or
stretch above 4 runs the donor-seam trial (`PrepareForwardSeamsAsync`), and
the same `TransferUvQuality.Measure` (per-face metric plus a whole-mesh
2,000,000-comparison overlap scan) ran on identical input several times:

- the legacy layout (target with the shell-transfer UVs): in the workflow, again
  as the `before` of every incremental pass and of the final check when no
  pass produced a mesh, and again at the start of surface recovery;
- each prepared mesh: after its pass, again as the next pass's `before`, again
  in the final check, and again in the workflow after it is returned.

None of these inputs changes once measured (the target and legacy UVs are
read-only here, and a prepared mesh is never edited after its pass), and the
measure is deterministic, so each layout is now measured once per call. The
workflow passes its legacy measure in and receives the returned mesh's.

Surface recovery also built its own copy of everything the seam passes had
already prepared: clean inputs, donor capture and charts, donor shells and the
legacy contact audit (the same 2,000,000-comparison scan with conflicts). With
shared passes (the default) it now reuses that preparation and projects with
its own options on a copy of the target, leaving the shared projection as it
is. Passes already share the donor and its charts because seam refinement only
reads them. `shareSeamPasses: false` keeps the independent control.

`SharedRecoveryAndReusedMeasuresMatchIndependentPreparation` compares the
published mesh, sources and reference with the independent control, checks
that the returned measure equals a fresh measure, and that recovery builds no
second set of donor charts.

Measurement pending: `Tools~/run-transfer-performance.ps1 -Repetitions 3
-Density 32`, control = main before this change, with
`compare-transfer-performance.py` confirming all exports are byte-identical.
The stages are "Forward donor seam trial", "Forward seam quality measures" and
"Forward seam contact audits".

## Reverse face projection on worker threads — 2026-10-21

`ProjectFaces` projected every target face in order on one thread: the chart
search, three corner snaps, four interior probes and the parent-footprint
coverage per face, for every LOD pass of Forward seam preparation and Reverse
(initial and refined faces, frozen shells, prepared chains). A face reads only
its own corners, the donor charts and their indexes, and writes only its own
three pixels and its record, so faces are independent. The only shared state
was the comparison budget counter, the chart-bounds candidate list and the
optional native (Embree) batch, which keeps per-surface state between faces.

Passes now run on worker threads when the native batch cannot be active
(query backend Legacy/Managed, or no query library) and the pass has at least
`ParallelProjectionMinimumFaces` (256) target faces. Each worker owns its
candidate buffer; each face writes a copy of its record, published in face
order afterwards. The budget stays exact: every face is charged from the
pass's starting count plus the charges other faces have already published.
That sum, plus the face's own running count, never exceeds the ordered pass's
total, so a face that exceeds the budget here means the ordered pass exceeds
it too, and a pass whose total is within the budget passes every check of the
ordered pass (each check compares a running count that is at most the total).
Once the published charges exceed the budget no further face starts, so an
over-budget input still stops after about one budget of work (plus the faces
already in flight) instead of projecting every face first. When any face
fails or the total exceeds the budget, the pixels are restored and the pass is
repeated in order, which reproduces the same result, exception and message. Cancellation still surfaces as
`OperationCanceledException`.

`ParallelFaceProjectionMatchesTheOrderedPassIncludingBudgetFailures` compares
pixels and every record field of both paths on a curved four-chart donor with
a hole, at budgets that pass exactly, fail by one query, fail halfway and fail
on the first face, and checks that an over-budget parallel pass leaves the
targets untouched.

Measured with `Tools~/run-transfer-performance.ps1 -Repetitions 3 -Density 32`
(`d393c61` → `371d90d`, Cafe_Table_A_01 / Modern_Kitchen_A / kamaz_Off, i9-14900K,
Unity 6000.2.6f2): every export is byte-identical, but the worker threads are
slower on all three models. "Face projection" in Forward 0.021 → 0.084, 0.79 →
2.36 and 4.84 → 8.68 s; "Forward pipeline" 0.45 → 0.68, 3.81 → 6.55 and 18.20 →
27.33 s; Reverse total 1.3–1.5×. "Parent footprint coverage", summed over workers,
rose 100–137× (Kamaz Forward 1.4 → 186 s over 50,429 calls), far beyond what
32 threads sharing the work would explain: the workers wait on each other.
The likely cause is allocation: the footprint check builds new lists for every
clipped edge and a LINQ region list for the tolerance fallback, and Unity's Mono
runtime uses the Boehm collector (`mono-2.0-bdwgc`), whose allocation lock and
stop-the-world collections serialize allocating threads. Not verified.

Result: rejected as the default. `ParallelProjectionMinimumFaces` defaults to
`int.MaxValue`, so every pass runs in order as before; the parallel path and its
equivalence test stay for a later attempt, which should first make the footprint
check and corner probes allocation-free and measure again.

## Reverse envelope audit: clip only cross-family pairs — 2026-10-21

After native placement, `ReverseUvNativeFamilyPacking.TryPackCore` scans the
packed family envelopes with `UvAtlasDiagnostics.Measure` and collects every
overlapping pair. Ancestors and their descendants share pixels in the packing
proxy by design, so most overlapping pairs are within one family, and the
decision then discarded them: only a cross-family conflict, an incomplete scan
or an invalid face refuses the trial. On Kamaz this audit took 4.9 s of the
exclusive Reverse solve (`TRANSFER_PERFORMANCE.md`).

`Measure` gained `crossChartOnly`. The envelope's chart of each corner is now
its face's family (material). A same-family pair is still charged to the
comparison budget at the same place in the scan, but it is neither clipped nor
counted. With the default budget (2,000,000) below `MaxConflicts` (4,194,304),
an incomplete scan can only come from the budget, so completeness, the
comparison count and every cross-family conflict match the whole scan: the
refusal decision is identical. Only the refusal log changes, from all
overlapping pairs to cross-family ones. `UvAtlasDiagnosticsTests` checks
equal comparisons and completeness at every budget.

The prepared cross-LOD footprint audit has the same shape, but its same-LOD
pairs rarely pass the bounding-box test, so it is left unchanged.

Measurement pending: `Tools~/run-transfer-performance.ps1 -Repetitions 3
-Density 32` control (main) against this branch, with
`compare-transfer-performance.py` confirming byte-identical exports; the gain
is the "Native envelope audit" sample.

## Reverse preparation: reuse an unchanged Full Pipeline result — 2026-10-21

Reverse with "Prepare with ordinary Transfer" ran the complete Full Pipeline
again on detached copies of the imported meshes, even right after the user ran
the same Full Pipeline on the scene entries. On Kamaz this detached preparation
is 16.2 s of the 37.3 s Reverse workflow (`TRANSFER_PERFORMANCE.md`), and the
paired benchmark already asserts that it reproduces the ordinary result byte
for byte.

A completed Full Pipeline now records, per included entry, the imported mesh
it consumed and the result it published, each by reference and by a raw
content fingerprint (`MeshAccess.Fingerprint`: vertex layout and streams,
indices, bounds, and skinning/blend shapes when present), plus its renderer
frame, source texture metric, forward UV reference and atlas size, and one
settings key over every context and workflow setting the pipeline reads.
The settings and inputs are taken before the run and must be unchanged after
it, because the asynchronous stages read materials and transforms while the
editor stays responsive.
Reverse preparation reuses that result only when the key equals the key of
the detached preparation it would build (shared atlas, finest included LOD as
source, every stage on, same copied settings) and every recorded fact still
holds. Otherwise it repeats the preparation exactly as before and logs why.
Scene edits are never reused, because the detached copy would take the
import's name. Reverse output is unchanged by construction: it receives copies
of the same meshes the detached run would produce.

`ForwardRunReuseTests` fail when a new context setting is neither keyed and
copied into the detached preparation nor listed as view state. The workflow
test checks reuse, a settings change and an in-place result edit.

Measurement pending: `Tools~/run-transfer-performance.ps1 -Repetitions 3
-Density 32` with `-DisableForwardReuse` (control) against the default run,
with `compare-transfer-performance.py` confirming all exports are
byte-identical. The cost of fingerprinting is the "Forward run fingerprint"
sample of the Forward pipeline.

## Auto-tune: choose attempts by measured UV defects — 2026-10-21

Full Pipeline auto-tune tries up to three binary-split separations and keeps
the best attempt. It scored an attempt as `shellsRejected + shellsOverlapFixed`,
with vertex coverage as the tie-break. `shellsOverlapFixed` only counts force3D
shells whose UV2 bounding boxes overlap. Collapsed UV faces, overlap pairs and
overlap area did not count at all, and seam-prepared targets always report zero
for both counters. An attempt with collapsed faces could therefore beat a clean
one that had a single force3D box overlap. The search also stopped as
"perfect" while targets still carried collapsed faces or overlaps.

Every target's final `TransferUvQuality` is already measured in the rendered
frame to decide the donor-seam trial. When the trial is accepted, the prepared
mesh is measured as well. Auto-tune now records that final quality per target,
re-measured after the publication clamp to [0,1] whenever the clamp moved a
coordinate, because clamping can collapse a face. Only aggregates are kept,
and only during an auto-tune attempt's transfer stage. Attempts are compared
lexicographically (`AutoTuneScore`), in this order:

1. targets whose transfer produced no UVs;
2. incomplete overlap scans;
3. invalid plus collapsed UV faces;
4. rejected shells;
5. summed overlap area (relative tolerance 1e-6), not pair count, which seam
   subdivision inflates;
6. the previous force3D counter;
7. vertex coverage.

Selection still needs a strict improvement. A perfect result now also requires
zero collapsed or invalid faces and zero overlap pairs. Zero-area 3D faces are
counted with `TransferUvQuality.Measure`'s own predicate and excluded. They
exist in every attempt and cannot be repaired by a separation.

This can select a different separation than before, so outputs may change
where attempts differ. The stricter perfect condition can also run more
attempts. PR #295 removes attempts whose symmetry split repeats.

Validation pending: Full Pipeline on the private corpus (PR #294 matrix,
`autoTuneChosen`), comparing collapsed faces, overlap area and time per model
against main. Unit tests cover the ordering, tolerance, perfect condition and
the geometry-degenerate count.

## Auto-tune: reuse attempts whose symmetry split repeats — 2026-10-21

Full Pipeline auto-tune runs symmetry split, repack and transfer for up to
three binary-split separations (10%, 5%, 20%). It stops early only after a
result with zero issues and at least 99% vertex coverage, which Kamaz never
reaches. The separation is read only by `ApplyBinarySplit`. A model without a
shell whose 3D separation ratio falls between the thresholds, a model whose
splits are all N-fold, and every run with the symmetry step skipped give all
attempts identical working meshes. Each such attempt still repeated the whole
repack and transfer.

After the symmetry stage, the pipeline streams the content of every included
working mesh (positions, normals, tangents, colours, all UV channels and
indices, but not its name) into one SHA-256 hash together with its split flag.
Names are excluded because retries restore "(Clone)"-suffixed copies; names
only label derived meshes and logs. If hashing fails, the attempt runs in
full. If an earlier attempt had the same hash, the attempt skips repack,
transfer and evaluation.
It restores that attempt's `HasRepack`/`HasTransfer` and stage outcomes, then
continues. Repack and transfer are deterministic for identical meshes and
settings, and a new best attempt requires a strict improvement, so a repeat
can neither be selected nor stop the search. The chosen meshes are restored
exactly as before. `UvTransferWorkflow.reuseIdenticalAutoTuneAttempts = false`
keeps the old path. A differential test compares both paths on a quad LOD
pair with transfer disabled: three repacks against one, with the same
published atlas bytes and stage outcomes.

Logs gain one Info line per reused attempt. Diagnostic captures omit that
attempt's repack/transfer stages because they were not executed.

Measured with the paired benchmark (`FrozenForwardReverseComparison`, density
32, Cafe_Table_A_01 / Modern_Kitchen_A / kamaz_Off), Unity 6000.2.6f2 batchmode
on an i9-14900K, three runs per arm, control `285147e` against `f2bcc88`.
`compare-transfer-performance.py`: all 120 exports per run have identical binary
numeric values; inputs, atlas dimensions and audit counts match. Medians (s):

| Model | Forward pipeline | Detached forward preparation | Forward repack (samples / s) | Forward transfer (samples / s) |
|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | 0.38 → 0.39 | 0.17 → 0.17 | 1 / 0.14 → 1 / 0.14 | 1 / 0.20 → 1 / 0.20 |
| Modern_Kitchen_A | 3.11 → 3.15 | 3.14 → 3.07 | 1 / 0.42 → 1 / 0.43 | 1 / 2.60 → 1 / 2.62 |
| kamaz_Off | 47.80 → 16.34 | 47.79 → 16.52 | 3 / 2.34 → 1 / 0.77 | 3 / 44.63 → 1 / 14.70 |

On Kamaz all three separations produce the same symmetry split, so two of the
three attempts are reused: shell transfer falls from 30 to 10 samples and the
donor-seam trial from 18 to 6. Cafe and Kitchen stop after their first attempt
and are unchanged. Reverse is unchanged (Kamaz exclusive 12.82 → 12.70 s,
overlap 29.85 → 29.80 s). How often attempts repeat across the furniture corpus
is left to the variant matrix's `autoTuneDistinctInputs` field (PR #294).

## Point-branch recovery: skip the contact scan without a single-point branch — 2026-10-21

`UvPointJunctionRecovery` runs on every unpacked chart in
`ReparameterizeStretchedShells`, both before packing and in the Reverse seed.
Charts are partitioned by full indexed edges, so a hard normal edge splits one
UV island into several edge-aliased patches. Those patches share two or more
position/UV aliases with their neighbours and can never rotate, yet each such
chart still paid one complete whole-chart contact scan with conflict
collection before the branch loop skipped them.

The anchors are now found first, from the same original coordinates, and the
chart returns before the scan when no non-main patch has exactly one anchor.
In that case the previous code also returned null, so UVs, refusals and the
anchor chosen for a rotatable branch are unchanged. A rotatable chart pays the
same scans and the same shared comparison budget as before. The early return
checks cancellation first, so a cancelled token still throws where the skipped
scan would have thrown.

Measured with the paired benchmark (density 32, Cafe_Table_A_01 /
Modern_Kitchen_A / kamaz_Off), Unity 6000.2.6f2 batchmode on an i9-14900K,
three runs per arm, control `76d1c36` against `285147e`.
`compare-transfer-performance.py`: all 120 exports per run have identical
binary numeric values; sample counts match. Medians (s):

| Model | Forward pipeline | Forward transfer | Reverse exclusive | Reverse overlap |
|---|---:|---:|---:|---:|
| Cafe_Table_A_01 | 0.39 → 0.38 | 0.21 → 0.20 | 1.66 → 1.62 | 2.90 → 2.84 |
| Modern_Kitchen_A | 3.18 → 3.11 | 2.66 → 2.60 | 4.32 → 4.27 | 5.78 → 5.66 |
| kamaz_Off | 49.61 → 47.80 | 46.21 → 44.63 | 12.79 → 12.82 | 31.24 → 29.85 |

The 1–5% differences stay within the run-to-run spread: exact, no regression,
no resolved gain.

Correction: "refusals are unchanged" did not hold for a chart with a
non-finite UV. The old code reached the shared-point lookup only after the scan
had refused such a chart (`invalidFaces > 0`). Looking the anchors up first
asked the counts dictionary for a key with NaN, which never equals itself, and
threw `KeyNotFoundException` instead of refusing.
`UvPointJunctionRecoveryTests.RefusalIsAtomic("invalid")` failed from this
commit on (local run on main, 2026-10-21). The lookup now uses `TryGetValue`:
such a vertex is no anchor, and the scan refuses the chart as before.

## Separate overlapped source branches at a single UV point — 2026-10-20

Some furniture charts join several edge-connected patches through only one
vertex. Their individual triangles can be isometric while the patches overlap,
so a stretch-only early exit misses the defect. Ordinary ARAP also lacks a
unique relative orientation at such a junction. A square with an overlapping
isometric triangle reproduces this in both mirrored and ordinary UVs.

After the existing ARAP/bevel passes, partition each chart by full indexed
edges. Keep the largest patch fixed. A branch may rotate only when it has one
shared anchor; exact position/UV aliases across duplicated vertex streams also
count as anchors. Apply one orientation-preserving rigid transform to the whole
branch. Never change positions, triangle indices, material UV0 or the shared
anchor. Charts with nonmanifold edges, invalid UVs or collapsed UV faces are
refused by this additional pass.

Try quarter turns before finer angles, at most 31 orientations per branch.
Check every moved face against its original intrinsic anisotropy (the existing
1% relative tolerance) and winding after Float32 rounding. The whole-chart
contact scan must complete, remove every contact between the moved branch and
the rest of the chart, and retain only pairs already present in the original
chart. Reducing pair count while leaving the branch covered is not accepted.
All attempts share 200,000 pair comparisons;
budget exhaustion or cancellation publishes no partial chart. Existing quality
thresholds and the native plugins are unchanged. Contacts whose area fits within
a Float32 coordinate-rounding band along the two triangle perimeters do not
trigger a rigid branch rotation. They remain in diagnostics and need precision
repair; the pair probes use the same shared comparison budget.

The supplied shelving LOD0 now has 25 overlap pairs instead of 33 and maximum
anisotropy about 152 instead of 171. Its frame retains zero contacts and zero
collapsed UV faces. The ordinary grouped-transfer LOD1 result has zero overlap
pairs instead of one; its pre-existing four degenerate faces and extreme
stretch remain. The kitchen countertop's near-contact is below the rounding
band, so its existing eight source/four target overlap pairs and extreme
stretch are retained. These source results agree
in off, legacy and adaptive symmetry modes. This is a bounded improvement,
not acceptance of either furniture model's complete lightmap layout.

Two portable overlap controls and all three private shelving modes fail before
the integration and pass after it. Kitchen controls additionally protect the
downstream target from regression. Copy-only source/export checks preserve exact oriented geometry and
authored FBX UV0; original fixture files remain untouched. Additional controls
cover clean charts, mirrored winding, cancellation, exhausted initial/search
budgets, nonmanifold edges, invalid input, duplicated anchor aliases and contact
slivers that must stay reported without a large rigid displacement. A covered
branch control reproduced a misleading pair-count improvement before the
complete-separation guard and now refuses the unchanged overlap atomically.

Exploration: replacing ARAP's Float32 rest frames with double/cross-product
frames recovered seven previously ignored narrow countertop triangles, but the
final packed result gained contacts and a collapsed face, so that change was
discarded. Forced boundary initialization and whole-chart planar projection did
not provide a clean general repair and are also excluded. Remaining continuous
chart folds and packed thin-triangle distortion need a separate experiment.
Rotating the kitchen's numerical contact initially reduced source overlap from
eight pairs to seven but increased ordinary target overlap from four to eleven;
even small-angle trials produced five. That regression is excluded by the
coordinate-uncertainty eligibility check, not by relaxing any diagnostic.

## Recover an invalid forward atlas from oriented surfaces — 2026-10-20

An overlapped UV result can have an apparently ideal stretch of 1. The ordinary
seam recovery then vetoes the correct donor with stretch 2 because it compares
against this invalid layout. Its early return also treated low stretch as
completion even when contacts remained. A two-quad control fails on the previous
seam preparation and passes with this recovery.

After the existing seam trials, try their established source projection with a
stricter normal agreement of 0.9. Retain the existing ambiguity, interior, parent
footprint, winding, source-quality and query-budget checks. Unsupported faces
keep their legacy coordinates. Healthy legacy faces retain their individual
metric bound; overlapped faces may use the existing absolute anisotropy limit 4.
Accept only a complete output with no overlap, invalid/degenerate triangles or
out-of-bounds UVs and maximum anisotropy at most 4. A partly repaired atlas is
refused. The caller can accept this clean result even when the invalid baseline's
scalar stretch looked better. This is an explicit change to comparison against
invalid faces, not a change to the absolute quality limits.

The detached copy preserves ordered Float32 triangles and every original corner
attribute except UV2. It carries the selected donor reference into subsequent
work. Projection runs off the editor thread in async mode and owns its inputs
through cancellation. Eight portable cases cover the failing overlap control,
unsupported and opposed surfaces, healthy-face protection, unchanged healthy
atlases, synchronous/asynchronous equivalence, and cancellation without leaks.
A bounded recovery refusal keeps the previously prepared mesh and its donor
reference instead of discarding it. The budget-one control reproduces the thrown
donor-tie comparison refusal before this guard; cancellation and unexpected errors
still propagate and clean up owned meshes.

Private Shelf_C validation separates asset repair from solver changes. Its LOD1
has four internal duplicate cap triangles and two bridge triangles without an
LOD0 counterpart. In a separate copy these six faces are removed and material
UV0 is projected from LOD0; the remaining positions and winding are retained.
Ordinary Full Pipeline on the complete body plus both doors produces 472 source
and 310 target triangles, with zero collapsed triangles and zero UV overlaps.
The target viewer score is 84/100 (body alone 85/100), maximum anisotropy 3.980
inherited from the door source and aligned area 0.999973. Model assets and private
diagnostic probes are not included in the package change.

Local Unity 6000.2.6f2 validation: 577 passed, zero failed, 19 optional skips,
including transfer, recovery, capture, performance and FBX round-trip suites.
The budget-refusal follow-up passes 146 related Transfer cases with zero failures.
The coverage job now requires TransferRescoreTests and ForwardSurfaceRecoveryTests.
A measured local run passes all 15 cases and covers 103/108 changed executable
lines across both Transfer fixes (95.4%), including all 26 rescoring lines that
the previous CI filter left unmeasured. The 80% Sonar threshold is unchanged.
The Playground control retains the preceding experiment's triangle counts,
0/12/911 overlap pairs, stretch and aligned-area measurements at LOD0/1/2.
It is deliberately not accepted as a partly repaired atlas. The separate Shelf_C
FBX copy passes per-triangle geometry, UV0, UV1 and submesh checks after import;
the existing small-UV-seam document pass is needed for the new FBX's close seams.

## Bound merged-shell rescoring by geometric correspondence — 2026-10-20

UV0 coverage could replace an exact geometric donor with a distant chart during
merged-shell rescoring. Two identical quads ten units apart reproduce the error
when only the target material UVs move to the other chart. The accepted rescore
also stored centroid distance in the surface-distance field, misleading later
deduplication and diagnostics.

Bound each alternative by sampled vertex and face-interior surface distance,
using the existing hint allowance (four times the original squared distance,
with a mesh-relative numerical floor). UV0 still ranks physically compatible
alternatives. Record the accepted surface metric rather than its centroid gap.
No projection, atlas, normal or quality thresholds are changed in this experiment.

All seven portable regressions fail on the previous implementation and pass
with the bound. They cover matching at scales 0.001/1/1000, complete UV output at
0.01/1/1000, an admissible coincident alternative with a different centroid, and
unchanged input mesh snapshots. The millimetre case tests matching only; this
experiment does not claim to repair every downstream projection limitation.

Local Unity 6000.2.6f2 validation: 416 passed, zero failed, 14 optional skips.
The Playground full-pipeline comparison retains 15,532/8,246/5,673 triangles,
0/12/911 overlap pairs and maximum anisotropy 2.557/2.493/380.866 at LOD0/1/2.
LOD1 aligned area increases from 0.9901909 to 0.9902186; other listed quality
metrics are unchanged. Existing coarse Playground defects remain explicit.

Shelf_C also contains incompatible/duplicate LOD1 surfaces, and its remaining
projection failures are a separate experiment. This bound alone is not a claim
that the original Shelf_C atlas is clean.

## Combined ready Transfer corrections (2026-10-17)

This PR combines the qualified selected-reference, complete donor attachment,
family alignment/packing, exact selected-shell vertex recovery and prepared
coarsest-rim corrections. It supersedes the earlier isolated local snapshots
below; their numbers describe their respective historical sources.

Current combined verification: 550 passed, zero failed, 16 optional skips across
three real models in both directions with Burst required; 85 clean public
regressions; both FBX compile variants. All 37,080 Reverse triangles preserve
ordered Float32 geometry, and every original quality, winding, provenance,
contact, precision and work gate remains in force. The affected wall retains
7/7/5 islands, aligned finest shells and a 428 px atlas at 80 texels/metre.
Its coarse Reverse maximum anisotropy improves from 2.19 to 1.17.

Remaining limitations are explicit: 313 middle-LOD per-face anisotropy increases
relative to the older independent native unwrap are real; the ordinary coarse
projection still has a face with anisotropy about 113. Full common-contour
correspondence for partial contributors is not established. Another model's
atlas placement and worst correspondence error change within existing gates.
This is a bounded correctness improvement, not completion of every Transfer
defect or the separate startup responsiveness work. Rejected private trials
and diagnostic capture hooks are excluded.

## Prepared Reverse foundation: recover a complete damaged rim — 2026-10-17

An existing shared-corner recovery can repair an extremely stretched face while
worsening its healthy neighbour. Prepare a complete damaged rim before freezing
the coarsest LOD. Keep the largest healthy connected main unchanged; develop the
complete smaller triangle-tree donor from its intrinsic geometry and weld only
its side of the existing common boundary. Refuse authored seams, nonmanifold
input, contacts, winding changes and incomplete development. This repairs the
shape of the entire damaged donor; it is not arbitrary per-vertex stitching.

Evaluate the established repair first. The alternative must fit every original
and established per-face metric and preserve the established exact corner-alias
partition in both directions. New contact trials use only the original budget
remaining after the established outer audits. Finer newborn-island repair keeps
the previous implementation. Existing density, precision and final packing
checks remain authoritative; no threshold or work limit is increased.

A local full run passed 546 tests, failed zero and skipped 16 optional cases,
including three real models in both Transfer directions with Burst required.
The clean portable package passed 81 cases; both FBX compile variants passed.
The new helper has zero analyzer findings with S3776/S3267 enabled, and the
complete analyzer diagnostic multiset matches the previous implementation.
A meaningful seam-continuity control fails on the preceding private candidate.

On the affected coarse model, worst Reverse anisotropy falls from 2.19 to 1.17;
its repaired rim faces are approximately 1.0. The atlas remains 428 pixels at
80 texels/metre, with the original triangle count, shells and continuous edges.
This is a partial local correction: the separate ordinary coarse projection
failure and finer comparative distortion remain unresolved. Other-model atlas
placement changes; unchanged-source subpixel exceptions require complete family
precision proofs. Passing these tests does not establish a complete contour
solution for every contributing shell across the LOD chain.



## Shared UV points and adjacent whole-island correspondence — 2026-10-16

Ordinary Transfer can leave a one-ULP split at a shared lightmap point when an
incident face retains an old overlap flag even though its projected UVs are now
healthy. Two established healthy neighbours may authorize a proposal for the
complete exact position/legacy-UV/donor group. Every incident face still passes
the current degeneracy, absolute/relative metric and winding checks; the existing
independent full-mesh contact audit decides whether the proposal is published.
The correction retains real legacy UV seams and different donor branches.

Frozen Reverse can then encounter adjacent fine islands whose independent fits
overlap. After all existing rigid and affine matches have failed, offer one rigid
translation of the complete later island using an exact shared surface point.
Only inherited, unambiguous same-parent boundary points provide constraints.
Disagreeing constraints are refused; accepted neighbours remain unchanged. All
original anchor-reach, relative/absolute metric, contact and work checks remain.
The rescue runs last so it cannot displace a successful affine correspondence.
No coordinate rounding, tolerance increase, face deletion or native change is used.

The unchanged copied inputs/settings run ordinary and exclusive Reverse on
Kamaz_Typhoon, CementWall and Old_Sofa. The CPU suite passes 471 tests, fails zero
and skips 16 optional cases, with Burst required. All 76510 output triangles match
exact geometry. Kamaz changes two ordinary UV triangles: both complete shared
edges at the LOD2 cabin now have equal endpoints. Its remaining 116/372 and 59/134
ordinary collapsed-face/overlap counts are unchanged. Reverse inherits 15346
faces versus 15312, with a 4509 versus 4519 px atlas at the same 256 tex/m; all
Reverse final atlases retain zero collapsed faces and unexpected overlaps.
CementWall and Old_Sofa retain all 26 stable outputs decoded-identical in both
directions, including the preceding Sofa topology repair.

Seven raw relative metric flags qualify under the existing precision policies:
complete cross-LOD family membership is identical, and one common similarity per
whole family has at most 0.000632 texel residual. One microscopic face uses the
existing 0.01 texel resolution rule; the other faces meet the existing rigid-family
altitude rule. A visible regression from premature rescue is rejected: an isolated
three-source/three-target bevel fixture fails before the ordering fix and passes
after it. Portable controls cover stale flags, genuine degeneracy/seams/donors,
boundary ambiguity, reach, cancellation and preservation of the affine result.
Both FBX compile configurations pass. Private capture hooks and imported models
are excluded from publication. These correctness runs are not a new performance
benchmark. Evidence: `_results~/forward-winding-review/boundaryprioritytrial-*`.

## Repair malformed frozen-foundation UV topology — 2026-10-16

Old_Sofa LOD2 contains a connected chart with a branched UV boundary and Euler
characteristic zero, although its geometric quotient is an orientable manifold
disk. A stretch/contact-only input audit leaves this internal UV slit in place.
Before cutting the frozen Reverse foundation, weld detached UV unknowns only
when their geometric quotient is a manifold disk and their existing UV topology
is not. Edge orientation, vertex fans and boundary topology reject geometric
holes, non-manifold contacts and required cuts on closed surfaces. Valid UV disks
are retained. Positions, texture UV0, triangle order and original meshes survive.

Use the existing ARAP iterations, with seed UV area expressed in the rest
geometry's units during the solve, then restore coordinate units. This prevents
texel density from tripping ARAP's existing area-collapse test. Every face retains
the caller's metric limit, existing relative tolerance and winding; complete
local contact and existing final atlas/padding/work audits remain mandatory.

Interactive preparation captures Unity UV arrays on the editor thread and runs
the numeric frozen initialization on its worker. ARAP and its conjugate-gradient
and Tutte solves observe cancellation before committing coordinates. The chart
acceptance check uses the caller's configured relative-stretch tolerance. A
strict-zero fixture and cancellation controls reject private mutants that ignore
these contracts; async preparation retains the synchronous output.

A portable connected twelve-face disk reproduces the internal hole: its
integration test fails on the old foundation and passes after preparation. All
17 portable controls pass, including reflected/scaled UVs, real holes, closed
surface cuts, cancellation and the comparison budget. Both FBX variants compile.
The full CPU suite requires actual Burst: 457 passed, zero failed, 16 optional
fixture/backend skips. Ordinary and Reverse runs include Kamaz, CementWall and
Old_Sofa. All 76,510 published triangles match unchanged geometry. Ordinary
outputs and both Kamaz/CementWall Reverse layouts retain their decoded values.

Old_Sofa Reverse LOD2 loses the malformed internal three-edge slit; maximum
stretch falls 3.553 to 2.959, P95 1.629 to 1.314, aligned area 50.77% to 56.58%,
with zero collapsed faces and unexpected overlaps. The only relative derivative
exception is a sub-0.001-texel face in an otherwise unchanged 847-face family:
all its corners fit one common similarity within 0.000167 texel, inside the
existing 0.01-texel precision policy. No tolerance is changed.

With the same Fill atlas setting, final packing selects 8x instead of 6x density
and a 1422 instead of 999 pixel atlas. This is a real output-size tradeoff, not
a fixed-resolution performance result. This slice prepares only the coarsest
frozen Reverse foundation. Ordinary Kamaz defects and four collapsed ordinary
Old_Sofa LOD2 faces remain. Local evidence: `_results~/forward-winding-review/`.

## Exclusive Reverse: reuse resident native charts and buffers — 2026-10-15

The existing xatlas API permits another PackCharts call after ComputeCharts.
Keep one resident atlas for one final exclusive frozen-family packing operation,
under its existing global native lease. Exact UV Float32 bits, indices and face
materials determine chart reuse. Operation-owned immutable footprint identities
also reuse the prepared flat/index/material arrays. Changed masks invalidate the
resident entry; failed preparation is never cached. Disposal, errors and
cancellation release the atlas/lease. No native ABI or plugin changes are needed.

Three alternating pairs use the same frozen quality/shared-prefix/Burst/PR253
control, copied FBX/import metadata, 32 tex/m, 2 px padding, narrow cuts, reach
0.05 and exclusive Reverse with Fill atlas. Kamaz still executes 22 placements,
but prepares charts 16 times with 6 cache hits; CementWall executes 12 placements
with 1 preparation and 11 hits. All 150 stable outputs, inputs and quality reports
match byte for byte. The input+chart preparation timer includes array construction
in both arms; its boundary is explicitly instrumented in the frozen control.

| Model | Reverse control / reuse (s) | Including preparation (s) | Input+chart preparation (s) |
|---|---:|---:|---:|
| Kamaz_Typhoon | 21.688 / 21.172 | 37.917 / 37.296 | 1.423 / 1.078 |
| CementWall | 2.067 / 2.030 | 2.717 / 2.689 | 0.0177 / 0.0015 |

The observed whole-Reverse reduction is only 2.38% / 1.78%; Kamaz timing ranges
overlap. Charts-only reuse had no resolved whole-solve gain. Buffer reuse removes
additional setup work, but placement and exact audits remain dominant. This is
not a large acceleration of the transfer or a live FPS measurement. Keep the
bounded resident implementation; a multi-context cache needs a different native
ownership API, not more live instances of the current singleton bridge.

The publication-only native/transfer regression suite passes 433 cases, fails
zero and skips 16 optional cases with actual Burst execution required. Six
additional timed runs each execute both required models without skips. The
published CPU candidate excludes the separate GPU prototype and its CPU scan
refactor; the later Sonar S3973 fix adds only an outer foreach block. CI compiles
both FBX variants and checks identifiers/dependencies. Evidence:
`_results~/native-chart-publication-check/`. Plugins and the owned project's
manifest/lock remain identical. Earlier combined cache/GPU measurements and
their separate NaN assertion rerun remain in `native-chart-reuse-final` and
`gpu-envelope-audit`; they are not the publication-only test count.

## Reverse final packing: port exact work trims — 2026-10-15

Port the managed PR253 trims onto local integration `846d08f5`, retaining its
whole-island matching, continuity and published-position quality fixes. Reuse
per-LOD face/index lists and surface group keys, avoid unused discrete orientation
ranking on aligned trials, skip the just-failed (0,0) rescue phase, and defer the
Float32 snapshot/shared-corner alias map until repair is needed. Repair order,
padding, Float32 and metric gates, density candidates and logical budgets stay
unchanged. A reordered precision test ensures already-validated faces still join
the aliases when a later face first needs correction.

Three alternating copied Kamaz/CementWall pairs retain all 150 stable output files
byte for byte and the input hashes/frames. Kamaz transform/validation median is
5.316 -> 2.933 s; nested Float32 stabilization is 0.459 -> 0.057 s. Native calls
remain 22, whole-atlas audits 13, and 63 duplicate failed family validations are
removed. Whole Reverse median is 28.275 -> 26.137 s, but workstation CPU/disk load
causes wide ranges, so the 7.6% difference is preliminary. CementWall shows no
whole-solve gain (2.131 -> 2.229 s). See `TRANSFER_PERFORMANCE.md` for all ranges.

Do not import PR254 concurrency as a proven accelerator: its older five-pair
study found no corpus gain and Kamaz exclusive was 6.5% slower. Keep its path out
of this continuation. Default native plugins are unchanged; Intel experiments
remain OFF. Burst direct-call/job/scalar integration from the preceding commit
is retained, but exclusive native family packing does not execute Burst raster.
Next inspect native placement/envelope work; do not reduce quality audits or
skip density candidates merely to lower runtime.
An independent capture records 22 requests (density 1/2/3/4/6/8: 2/2/6/4/4/4
calls). Its first proxy has 98122 triangles for 2762 families versus 31842
published chain triangles, and 66736 proxy faces have altitude <=0.01001 base
texel. These guards/connectors are not exported faces or a safe removal list.
The capture keeps all 37 stable Kamaz outputs identical and is excluded from
performance medians.

426 regression tests pass, 0 fail, 16 optional private skips, Burst required.
Both FBX compile variants and identifier/dependency guards pass. Test project
manifest/lock restored exactly. Artifacts: `_results~/transfer-packing-audit/`.

## Whole-island correspondence with inconsistent bevel anchors — 2026-10-15

Baseline: main `1945a383` (1.2.10). The reported CementWall case reproduces
with ordinary preparation, Legacy Fixed symmetry, 32 tex/m, 2 px padding,
narrow junction cuts enabled, and exclusive frozen Reverse UV at reach 0.05.
Baseline: 388 inherited / 1750 new faces, 2142 px atlas. A failed whole-island
similarity mixes anchors from large surfaces and inconsistent small bevels;
rejecting that proposal makes otherwise corresponding whole charts new families.

Keep the existing complete-anchor fit first. On failure, test at most 32
triangle-seeded similarities, ordered by anchor surface area. A proposal needs
complete three-corner agreement on at least 80% of that candidate's geometric
anchor area; refit that support together. The complete island retains one
similarity/reflection, with unchanged metric and sibling-contact audits. Faces
whose anchors do not fit are extensions, never fabricated inherited faces.
Successful similarity matches retain priority across all candidate donors.
If none passes, one full-rank affine fit may deform the complete island using
all of a candidate's face anchors. Every anchor must fit within the existing
projection budget. Every face must also retain the existing absolute metric
limit and at most the existing 1% relative stretch increase; complete-island and
sibling-contact audits still apply. Singular/non-finite proposals are refused.
No finer triangle subdivision, tolerance relaxation or native plugin changes
are introduced.

The same reproduction exposed an independent bounded-footprint subtraction bug:
an infinite supporting edge fragmented remainders even when the bounded donor
had no positive-area intersection. Keep such a remainder intact. Dense 32x32
donor controls, with and without a real interior hole, exhaust the old 2048-piece
limit at 72927 comparisons; the corrected subtraction completes both without
changing that limit, the work budget or the texel band.

The CementWall trial completes with 1079 inherited / 1059 new faces and a
1522 px atlas. The large LOD0 islands have whole-island correspondences; new face
counts include extensions and do not mean every such face is a separate island.
The existing publication quality audits pass. Private FBX files are not committed;
the opt-in paired harness retains model hashes and verifies input immutability.
Artifacts and rejected controls: `_results~/transfer-shell-diagnostics/`.

Kamaz's current importer settings reproduce ordinary donor-seam recovery changing
the total LOD2 triangle count from 3158 to 3264. Reverse repeats that preparation.
Two retained front points acquire differences of 1.19e-7 and 1.79e-7 UV, with the
same donor labels. Vertex splitting at these numeric discrepancies can fragment
an intact UV2 island; UV0/normal boundaries do not necessarily bound that island.

The recovery trial now preserves shared original position/UV2 points for the same
donor. Only healthy, originally non-overlapping faces qualify. Samples must agree
within the existing texel band; incompatible samples and distinct donor seams
stay separate. Every snapped point retains the absolute and relative metric
checks. A bounded audit checks all changed faces against every other face using
the existing overlap predicates; an uncertifiable trial retains the original
successful recovery. Interactive execution runs this additional CPU work on a
worker and awaits cancellation before releasing owned data.

Review follow-up: stretch and absolute area do not detect a reflected face.
Preserve the sign of each healthy face's UV area both before accepting a shared
point and in the final continuity audit. Existing mirrored winding remains valid;
no metric, contact, padding or work limits change. Six orientation cases expose
four failures before the fix and pass afterward, including both winding signs
and unchanged-winding controls. The CPU-only publication suite passes 439 tests,
fails zero and skips 16 optional cases with Burst required. All 50 stable model
files retain identical decoded values, including double bits; four JSON files
differ only in equivalent final-digit numeric formatting. Both FBX compile
variants and identifier/dependency guards pass. Evidence:
`_results~/forward-winding-review/`. This guard does not remove the existing
ordinary Kamaz collapsed-UV and overlap defects.

On the copied Kamaz, body LOD2 has 726 UV2 connected components instead of 788,
and the checked front points have no new discontinuities. Body triangles remain
3148 (3264 across all five meshes). Native donor-seam subdivision is not disabled.
The unified Forward LOD2 audit retains the previous 59 collapsed faces and 134
unexpected overlap pairs: those remaining defects are not claimed solved here.
Reverse publication audits pass, with no new fine triangle splits.

The seam validator also used repeated Float32 vector operations for barycentric
positions, while the actual mesh writer accumulates decoded attributes in double
and rounds once. A copied Kamaz triangle supplies a regression where the former
validator collapses two corners but the published mesh retains positive area.
The validator, refreshed metric frames and shared projection positions now use
the writer's interpolation. New projection positions are reconstructed from the
original face/barycentric map and the captured projection origin. No precision
or quality limit is relaxed, and original corners/streams remain unchanged.
Kamaz LOD1 has 12537 triangles rather than 12546; LOD2 remains at 3264. Its 59
collapsed UV faces / 134 unexpected overlap pairs remain unresolved. Reverse
after this correction has 15312 inherited faces / 2762 packing families, a
4519 px atlas, and no overlaps or finer cuts.

With the affine fallback, Kamaz inherits 15311 faces rather than 15295, with
2763 rather than 2766 packing families. The atlas is 4519 rather than 4507 px;
this is a correspondence improvement, not a packing-size or speed claim.
CementWall remains at 1079 inherited faces / 1522 px. These comparisons use the
same copied inputs and settings; all Reverse overlap and split counts are zero.

The discarded synchronous source guard built complete shell descriptors and
streams only to check channel presence. Metadata now supplies that check; worker
transfer analysis and cache invalidation of LOD hints remain intact. One copied
Kamaz sample measured the old guard at 22.1 ms versus 0.17 ms including reflection
for metadata. Diagnostic full-pipeline samples were 36.10 / 37.81 s, and detached
preparation 31.42 / 32.46 s. These include capture/validation and are not a claim
of live responsiveness or a statistically resolved total speed improvement.

Validation: 194 passed / 4 optional private-fixture skips in the full model and
focused regression run; a subsequent 131-test worker/capture/CementWall run also
passes. Both compile-check FBX configurations pass. Original input snapshots
remain unchanged. A trial protecting healthy faces increased subdivisions
elsewhere; unconditional seam retention caused an extra overlap; limiting each
cut to its local segment risks internal T-junctions. Those trials are not retained.
The expanded affine/position regression and copied-model run passes 204 tests
with 4 optional private-fixture skips. A partition-backtracking trial and a
planar boundary projection trial did not reduce the remaining forward defects;
neither is retained.
A subtexel cut-line snapping trial also failed the existing unsafe-corner
regression (it introduced a cut where complete inherited triangles must stay
intact). It is discarded, with that regression and the cut precision limits
unchanged.
Final exact-source regression run (including the broader Reverse UV suite):
300 passed / 6 optional fixture skips / 0 failed. Both FBX compile variants,
identifier guard and tool dependency checks pass. The copied-model results
above are from `published-position`; `final-transfer` contains the final
regression XML/log. New front discontinuities remain zero, with 726 connected
body UV2 components on Kamaz LOD2.

## Forward transfer: per-shell work without whole-mesh copies — 2026-10-14

The paired performance runs exclude ordinary (forward) Transfer, and a code
read of that path found per-shell work that scales with the whole mesh:

- `SymmetrySplitShells` built every shell snapshot from fresh `mesh.vertices`
  and `mesh.normals`, two full array copies per shell (per prescribed parameter
  × shell in `SplitWithParams`). The streams are now read once and re-read only
  where the existing code already re-reads them, after an applied split (the
  split helpers write the mesh exactly when they return a nonzero count).
  `SplitWithParams` also memoizes a shell's UV0 overlap until the next split
  instead of rescanning every shell for every parameter and both match passes.
- Per-shell overlap scans (`HasUv0Overlap`, `TransferCandidateQuality`
  overlap, `CandidateHasOverlap`) allocated whole-mesh UV and chart arrays per
  shell and candidate. `UvAtlasDiagnostics.ShellGeometry` renumbers the shell's
  vertices into compact arrays. Face order and corner coordinates are unchanged,
  so the scan's triangles, grid, pairs, area sum and completeness are the same;
  only `outOfBoundsVertices`, which none of these callers reads, now counts the
  shell's vertices instead of the whole mesh.
- The diagnostic-only Phase 2a-cov 3D coverage pass (a whole-source BVH plus one
  query per target face, used only for a Verbose log) runs only when that log
  is enabled.

No transfer decision, UV, ordering or tolerance changes. The paired benchmark
times the complete ordinary pipeline as `Forward pipeline` (export excluded) and
the detached re-run as `Detached forward preparation`. Measured on the private
three-model corpus (Intel Core i9-14900K, Unity 6000.2.6f2, 32 tex/m, 2 px
padding, Fill atlas): a = `a5c5deb`; b/c = `3552a3c5`, which carries this change
plus reverse-only packing and Burst changes that the forward pipeline does not
call (b and c differ only in reverse validation concurrency). Medians of three
runs of a and five of b and c:

| Model | Forward a (s) | Forward b (s) | Forward c (s) | Detached a (s) | Detached c (s) |
|---|---:|---:|---:|---:|---:|
| Cafe_Table_A_01 | 0.53 | 0.46 | 0.47 | 0.30 | 0.24 |
| Modern_Kitchen_A | 4.40 | 3.86 | 3.84 | 4.23 | 3.91 |
| Kamaz (`kamaz_Off`) | 30.39 | 28.09 | 27.78 | 29.82 | 28.93 |

Ordinary Transfer is about 13% faster on Kitchen and 9% on Kamaz (a → c); Cafe
is too small to resolve. The detached preparation gains less (8% / 3%). Forward
outputs and all 120 exported geometry/UV/ancestry/quality files are
byte-identical across all arms. Artifacts:
`_results~/transfer-performance-20261014-eighth/`.

## Transfer performance: immutable footprints and equivalent placement search — 2026-10-13

Baseline `97d8828f` (1.1.52), branch `codex/transfer-performance`.
This experiment adds opt-in stage timing and counters for the paired forward /
reverse benchmark, then reuses immutable whole-family proxy footprints within
one final pack. It does not change density candidates, rotations, overlap rules,
shell correspondence, Float32 repair or publication audits. Cache retention is
bounded and no cache survives a final-pack call. Ordinary Transfer,
`GroupedShellTransfer`, `XatlasRepack` and `SymmetrySplitShells` are unchanged.

The continuation profiles fallback candidate search separately from placement
audits. Its indexed occupancy lookup must keep the complete candidate trace,
refusals and work budgets equal to the original traversal. Both overlap policies
are checked on the same three private models and all exported geometry, UV and
ancestry reports are compared by their exact binary numeric values (without
tolerance); JSON decimal formatting may differ at the final printed digit.

The native continuation replaces only bit-image intersection and padding
dilation traversal. It must reproduce the original clipped storage-word
predicate and every eight-neighbour dilation pixel exactly. Local
native builds use `MESHLAB_COPY_PLUGINS=OFF`; plugin publication still requires
`build-native.yml`. Differential mask tests and replay of every real native
request cover both overlap policies; managed final UV audits remain unchanged.

The next managed continuation indexes the first colliding mask span for integer
x positions on a repeatedly searched occupancy row. The index is local to one
Find call and one immutable row window. Preparation is bounded; a partial final
span resumes at the original prefix. Logical work accounting must stop at the
same over-budget span, and every candidate/audit trace must stay identical.

The native search continuation owns one immutable integer occupancy snapshot
per Find call. It advances through occupied candidates in bounded chunks and
returns the next free candidate to the existing managed placement/UV audit.
Logical work, row/span priority, jumps, phase retries and all stop conditions
must match. Chunks resume inside a long span query so cancellation remains
responsive. Older plugins fall back to managed search. Private native builds
are loaded only by explicit test configuration; package binaries remain
published through `build-native.yml`.

The combined continuation binds all xatlas calls in the private benchmark to
the same rebuilt library as footprint search. Version 2 additionally shares
immutable occupancy rows between rotations of one family; mask cursors remain
private to each Find. Snapshot owners are recreated after occupancy changes,
and native searches retain their row data until disposal. Legacy exports and
managed fallback remain compatible.

An optional Editor assembly uses Burst/Collections to compute triangle raster
spans in parallel with Strict floating-point mode. Spans are merged in the
original triangle/row order. Small masks and bounded oversized buffers retain
the scalar path. A BurstDiscard execution witness prevents reporting managed
jobs as accelerated. The benchmark scopes compiler runtime flags without
writing EditorPrefs. Every native/Burst result must retain the exact baseline
UV, geometry, ancestry, work budgets and packing decisions.

The Intel oneTBB continuation is an opt-in native build experiment. Large
brute-force chart windows evaluate independent read-only rows in an eight-thread
arena. Reduction preserves first-inside early return, rotation priority and
stable metric/distance ties; chart commits and random placement remain serial.
Small windows and the first eight rows retain scalar traversal. The normal
build stays dependency-free until native replay and the paired corpus establish
a measurable benefit. oneTBB sources are pinned to 2021.12.0 commit
`9afd759b72c0c233cd5ea3c3c06b0894c9da9c54`. Burst and shared snapshots stay enabled
in both benchmark arms.

The first full oneTBB trial preserves every UV bit but slows Kamaz overlap by
about 6%; native-only timing is insufficient to qualify it. The revised row
search stops already-running late rows after an earlier inside fit, shares only
a strictly lower feasible metric for pruning (equal metrics remain eligible),
and allocates the parallel row table only after the scalar prefix fails. Both
native replay and complete Unity solve measurements must be repeated.

Revised final result: Cafe whole reverse solve 1.165s -> 0.746s without overlap,
2.875s -> 1.698s with overlap (36% / 41% reduction). Kitchen gains about 3% / 1%;
Kamaz is effectively unchanged (16.370s -> 16.351s / 34.173s -> 33.736s), so the
initial slowdown is removed. Six fresh final runs retain all 120 baseline
exports' exact numeric bits, counts and packing decisions; both full suites
pass 288/305 with 17 optional skips and zero failures. Six native suites and all
74 recorded requests also pass on the revised source. Real Burst work remains
unchanged in both arms. The oneTBB option stays experimental/OFF; publishing an
ON build is blocked without native runtime integration. See the seventh
continuation in TRANSFER_PERFORMANCE.md for artifacts, limits and the earlier
unreproduced ordinary Kamaz NullReferenceException.

The baseline runs at 32 tex/m show final packing dominating reverse solve on
Cafe Table, Modern Kitchen and Kamaz. Therefore the first optimization targets
repeated footprint preparation rather than GPU projection. A faster query
backend remains a separate experiment after packing costs are reduced.
Reproduction, timing semantics and verification:
[TRANSFER_PERFORMANCE.md](TRANSFER_PERFORMANCE.md).
Artifacts: `_results~/transfer-performance-20261013/`.

Intel-library continuation, 2026-10-14: optional ISPC integer masks preserve
all 74 native requests and all 120 complete UV exports, but do not improve
packing and regress Cafe overlap by roughly 9%; the backend stays OFF.
Embree CPU query batches on the three real model geometries are faster on
Kitchen/Kamaz, but direct primitive traversal changes boundary hits and tied
donor UVs. It is a private query experiment, not an enabled transfer/bake path.
The IPP byte-mask adapter is exact but loses to existing word dilation after
conversion; the separate 1u adapter fails exact comparison. oneMKL was assessed
against the packing and managed ARAP code, not numerically benchmarked.
See [INTEL_LIBRARY_BENCHMARKS.md](INTEL_LIBRARY_BENCHMARKS.md) for full timing
scope, compatibility failures, pinned dependencies and reproduction.

Compatible Embree continuation: discovery/proposals now feed the original
managed primitive tests and tie order. Three final processes pass 956,040 strict
result-bit comparisons on nine fixtures; actual 64²/128² bake bands retain all
map bytes and counters on Cafe/Kitchen/Kamaz. Large Kamaz nearest batches gain
1.54x/1.62x, but small batches and Kitchen rays regress. This is a private
prototype, not a production/default backend or complete reverse speed claim.
See [EMBREE_COMPATIBILITY.md](EMBREE_COMPATIBILITY.md).

Production-path continuation: Reverse centre queries now use an optional typed
backend and bounded buffers local to one projection call. Per-chart native
batches were usually too small on the real corpus, so one global donor tree
discovers full-reach candidates, followed by exact managed resolution within
each original chart. A global nearest proposal must never narrow another
chart's search. Small batches, missing plugins, oversized donor inputs and
candidate overflow keep the managed path. Final UVs, ancestry, ambiguity,
comparison budgets and packing decisions remain the strict qualification gate.
The optional `meshlab-queries` target builds from `Native~/src/embree_queries.cpp`
with pinned Embree 4.3.3; runtime publication stays gated by native CI and
dependency/license integration. Paired full-workflow measurements and limits:
[REVERSE_QUERY_BACKEND.md](REVERSE_QUERY_BACKEND.md).

## Remesh chart merge continuation: fitted thin parts — 2026-10-10

- Continuation after native voxel-2 position correction: three CafeChair
  roundoff faces produce zero-area UVs under all three charting variants.
  Surface-fit revision 2 uses the healthy adjoining normal only for final
  regularization of float-roundoff faces, under existing feature/topology/
  surface audits. Six scale/protected-edge controls pass. Exact Simplify inputs
  and rejection counters are retained; clean UV certification still does not
  imply acceptable stretch (12,219.9 on the intermediate CafeChair repair).
  Its completed saved-setting case selects a certified broad result: 30,128
  faces / 663 islands, mean/worst stretch 1.07793/7.36968, zero UV defects,
  1,557.9 seconds. The narrow 896-island packed candidate is refused for one
  tiny same-chart intersection; certification is not relaxed. The long case
  passes, while final corrected public controls run separately from its three
  outdated prototype fixture failures.
  Packing controls use byte-identical Simplify inputs: no merge finishes in
  62.9 seconds but retains 3,641 islands / worst stretch 12,216.9. Fast packing
  plus merge reaches a certified 898-island / 3.89556 narrow checkpoint, then
  is stopped after the 30-minute research budget without a full-case pass.
  The remaining broad bracket cannot beat 898 islands after its rejected
  upper budget falls to 2,233 joins; early exit and rejected-pair reuse remain
  proposed performance work. See the replay's packing-control table.

- Same Remesh UV0 chart-merge experiment; baseline `fc62cc8`. Legacy transfer,
  `GroupedShellTransfer`, `XatlasRepack` and `SymmetrySplitShells` are unchanged.
- Coarse fitting includes long patches next to short cross-section edges and
  retries reduced motion under the existing topology/distance gates. Broad UV
  merging must preserve the quality already achieved by narrow merging, rather
  than spend the original atlas's larger stretch allowance.
- Historical voxel-1 Park at 64 / Solve On: same 2,946 triangles, target RMS 0.0415130 → 0.0405652;
  112 → 113 islands, worst stretch 2.85105 → 3.12150, zero overlaps/degenerates/OOB.
  Reverse maximum rises within the unchanged surface gate. The 83-island variant
  with worst stretch 5.38553 is rejected. This does not solve fragmentation or
  coarse-voxel thickness. Garbage_Chute and Park Solve Off retain their metrics.
- At 128, Solve Off fails native unwrap; Solve On improves distances but retains
  worst stretch 14.2755. These research rows are not quality acceptance evidence.
- Protocol and measurements: [surface fitting replay](REMESH_SURFACE_FIT_REPRO.md).

## Progressive reverse atlas — 2026-10-09

### Mandatory final packing independent of seed preparation — 2026-10-11

Same PR225 experiment, continuation from `6f3f66d0`. The editor workflow always
requests final shared-family packing, including when it consumes an existing
coarsest-LOD UV2 layout from Repack or Transfer. Optional uniform filling controls only the common scale;
it no longer depends on seed preparation. Intermediate vacancy placement remains
part of projection, but cannot become a successful final atlas by itself.

The final pack no longer adopts the entire intermediate coordinate array when
rectangle packing fails or a fresh placement has a higher bbox cost. A rectangle
refusal tries complete triangle-footprint packing on an empty canvas instead,
with no old family placements pinned. Further optional improvement starts from
that completed fresh pack. An unsuccessful required final pack is reported as
failure before publishing any mesh, preserving the previous editor result.
All ancestry/overlay families retain one rigid move and all LOD audits still
gate publication. Controls cover preparation/filling independently, actual donor
movement, projected detail ancestry, empty-canvas ring nesting and final-packing
failure without intermediate publication.

Verification: 358 EditMode cases pass, zero fail, and 28 optional fixture cases
are skipped; the last donor-selection/status edits pass five additional focused
controls. Twelve actual FBX Reverse chains complete across three models, both
overlap policies and both seed paths, with zero collapsed/invalid faces and zero
unexpected overlap pairs. All 30 Forward exports match the preceding baseline.
Both FBX reference-compile variants pass. Kamaz fragmentation and visible empty
regions remain; these checks certify completed packing, not optimal density.

Artifacts: `_results~/reverse-full-repack-20261011/`.

### Stable donor charts, local cut retention and native family envelopes — 2026-10-11

Continuation of the same PR225 experiment, baseline `160d976a`, with main
`9ab555c9` merged in `8459785`. New finer-LOD parameterization is prepared on
the complete detached input, using authored charts, local ARAP and planar
lightmap-only joins across texture seams. Retries interpolate that immutable
parameterization instead of re-unwrapping whichever faces projection leaves
unsupported. UV0, mesh streams, source geometry and source FBXs are preserved.

Strip subdivision recreates face records, so ordered overlap references are
rebuilt against the current records before chart allocation. Intrinsic rescue
flags reflect actual packing repairs, including valid local triangles whose
altitude disappears after Float32 translation. Previously explicit strip
regions are not subdivided a second time by the legacy junction finder.

Refinement protects the complete donor/recipient patch and donor ancestors,
keeps baseline overlay ordering, and prioritizes protected inheritance during
the first contact arbitration. If a patch retry cannot advance, optional cuts
are retained for that input and its immediate donor; unrelated inputs can
still keep their cuts. Publication retains coverage, stretch and contact gates.

Repeated overlap audits now start from the original projection layer, rather
than the rank assigned by the previous arbitration. Removing or recutting a
neighbour cannot accumulate stale overlay ranks. Seam copies retain this
scratch state; existing serialized ancestry fields are unchanged.
Compact seed preparation propagates the actual packed density to finer charts.
Current-LOD underlay rollback rebuilds support flags from surviving projections,
so refused details cannot leave orphaned support in the contact audit. These
address the two Reverse findings in review `5484582384`.

Final packing additionally tests xatlas's existing PreserveShape UV packing on
the actual triangle union of each ancestry family. Disconnected pieces receive
short connecting proxy triangles for native chart discovery; a bounded search
can fall back to a conservative envelope. Precision-sensitive families can be
packed in a low-coordinate header without an intermediate Float32 rescale. No
proxy triangle is exported. All descendants and ordered overlays receive the
same quarter turn, common scale and translation. Native normalized output is
converted back using its actual width/height, and a larger common canvas is
accepted only under the atlas limit and a lower bbox cost per unit density.
Sub-ULP phase retries occur before Float32 rounding; complete family contacts
and actual exported triangle metrics still gate every result. Additional proxy
connectors can reserve part of a concavity. The native candidate is optional
and wins only when its bbox cost improves; valid existing shells are retained
otherwise. Triangle-footprint compaction and ring controls remain in place.
Packing also tests twice the common pixel density in a larger canvas, with the
same two-pixel gutter. It accepts this only at a lower bbox cost per unit
density. This reduces the gutter overhead of thousands of small charts without
stretching, shrinking, or independently moving inherited family members.

Artifacts: `_results~/reverse-cuts-pack-20261011/`. The complete fresh model
comparison and test counts are recorded in
[FORWARD_REVERSE_COMPARISON.md](FORWARD_REVERSE_COMPARISON.md).

### Authored-chart seed preparation by default — 2026-10-11

Continuation of the same progressive Reverse experiment on PR225, baseline
`81e9fb59`. Coarsest-LOD preparation now retains authored UV0 chart domains,
uses the ordinary Repack ARAP repair in world metric, and applies the existing
per-chart normalization, intrinsic-face rescue and audited seed packing.
Matching 3D/UV edges weld only chart scratch topology. Source mesh attributes,
material slots and original face identity stay detached and preserved.
Independent renderers and point contacts keep separate domains. UV0 overlaps
between different domains are resolved by packing; self contacts still trigger
local repair, and packed UV2 receives the full cross-chart contact audit.

Unity seed charting is an explicit disabled-by-default option, never an implicit
fallback. Existing seed cut, barycentric ancestry, projection and final family
packing checks remain active. The shared Repack ARAP implementation is exposed
internally with cancellation; its ordinary Transfer callers retain their
previous settings and behaviour. Local unsupported finer-LOD charting is a
separate path. Benchmark and regression evidence is recorded in
`_results~/source-seed-default-20261011/`.

Fresh full-pipeline runs complete all six Cafe/Kitchen/Kamaz policies at
32 tex/m with no collapsed UV faces or unexpected contacts in any LOD.
Thirty Forward mesh exports are byte-identical to the previous baseline.
The final graphics-enabled run passes 331 tests, skips 34 optional cases and
has no failures; both FBX reference compilation variants pass. Some donor and
seed junction trials still refuse and retain an audited uncut layout. Packing
is not uniformly better than the Unity seed. See the authored-chart section
of [FORWARD_REVERSE_COMPARISON.md](FORWARD_REVERSE_COMPARISON.md) for measurements.

### Actual UV components before shared-family packing — 2026-10-11

Same progressive Reverse experiment on PR225; baseline `a51de228`. A completed
Kamaz exclusive atlas retained region labels across independent placement of
disconnected UV pieces. Grouping by those labels reserved 18.35 million pixels
of rectangles in a 1.05-million-pixel canvas. Actual component families need
approximately 0.45 million padded rectangle pixels at the same density. Final
packing now finds geometric/UV corner-connected components within each surface,
then joins explicit donor ancestry and ordered overlays. Corner-connected
region pieces, including the ring control's T-junction boundaries, retain one
motion. A reused label cannot pin separated islands to their original gaps.

Preliminary tightening also exhausted the budget shared with every fresh
footprint order. The orders now have independent bounded budgets, and their
work/refusal diagnostics are retained. This does not remove precision, stretch,
positive-area, overlap or gutter requirements. Frozen completed-chain replays
check every donor/descendant and underlay/detail relation against a common
quarter-turn, scale and translation, separately from the full pipeline runs.
Source geometry and UV0 are unchanged. Artifacts: `_results~/finalpack-20261011/`.
The completed model and regression results are recorded in
[FORWARD_REVERSE_COMPARISON.md](FORWARD_REVERSE_COMPARISON.md).

### Current-LOD underlays and triangulation-independent strips — 2026-10-11

Same progressive Reverse experiment on PR225, baseline `eccca7d1`. The attached
raised-rim and new same-LOD underlay controls both reproduce failures in the old
overlap path. New detail can now inherit UVs from newly introduced lower geometry
in its own LOD. Lower dependencies are resolved geometrically before mapping;
normal, ambiguity, metric and complete footprint checks remain active. Coplanar
independent renderers remain separate. Optional projection refusal restores its
allocated UVs and dependent detail instead of leaving an unclassified overlap.
The additive `projectionSupport` provenance is understood by the independent
audit and Viewer as well as the transfer's ordered contact graph.

UV-plane subdivisions replace tessellation-dependent cuts on unpublished seed
charts and newly unsupported finer geometry. Barycentric source maps compose
through donor and junction cuts; original material order is retained. Neighbouring
pieces may rejoin as a compact rectangle and new strips align to their compact
axis when rotation is enabled. Inherited family members are not recut or moved
independently. Ordinary source repacking retains its existing-edge cutting path;
the interim attempt to share interior cuts with the legacy matcher regressed
Kamaz's forward distortion and was removed before publication.

The final packing improvement rasterizes the union of all triangles in each
family across the LOD chain. A padded ring can admit an unrelated smaller family
inside its hole; a duplicated overlay remains at the same transformed coordinates
as its underlying faces. This is a same-size improvement of the valid rectangle
placement, not a claim of optimal packing. Metric/contact validation still checks
actual exported coordinates; placements unable to improve extent skip expensive
geometry validation.

Research, invariants and executable control details:
[REVERSE_UV_UNDERLAYS.md](REVERSE_UV_UNDERLAYS.md). The 45-case control run passes,
including compactness and source-area tests, sparse T triangulation, three scales,
reflection, stacked underlays, renderer order and actual post-packing UV equality.
The first long corpus attempt was cancelled after exposing repeated validation
work; its partial passes are not reported as full regression validation.
Final corpus results follow after the complete fresh run.

### Scalable shared-family final packing — 2026-10-11

Continuation after the paired Forward/Reverse visual comparison rejected the
Kamaz and Kitchen results. The seed, new-chart rescue, projection metric,
refinement comparison and final export audit now use source-local triangle
corners transformed in double precision. BVH positions still share the same
relative world frame. Rounding those positions before measuring a nearly
collinear face could change its intrinsic metric and make a valid working
layout fail the exported-mesh audit. Neither positive-area faces nor vertex
attributes are removed to hide this failure; the stretch limit remains 4.

Independent rescue triangles test their actual placement before reserving a
seed cell. Fragile charts have priority near the atlas origin. The footprint
packer also starts from validated family coordinates and moves families
individually, so one immovable family does not discard every other improvement.
A refused optional density increase retains the tighter valid layout. Rectangle
packing cannot replace an existing layout with worse bbox cost at equal density;
a rectangle refusal still permits compaction of the validated triangle union.

Private paired captures at 10/32 tex/m remain distinct from acceptance tests:
they record a refused mode instead of calling it a successful Reverse result.
The intermediate source-metric run completes both Kitchen policies at 32 tex/m
with no collapsed faces or unexpected overlaps, but its 11.5/11.6% LOD0 bbox
fills are still unacceptable. Kitchen at 10 tex/m still refuses a thin seed
face. Final family fallback/compaction results and the frozen corpus gate must
be checked before publishing this continuation.

Refined source frames are reconstructed from editor-thread snapshots rather
than reading Mesh accessors on the projection worker. Viewer assessment also
retains the original local corners and transform: converting the whole mesh to
Float32 world positions first manufactured stretch on the thin captured faces.
Zero-length geometric edges keep the existing degenerate classification.

Final family candidates are rigid transforms or one common scale. Their 1%
relative derivative preservation gate now applies to texel-resolved triangles;
when both old and new minimum altitudes fit the configured subpixel boundary
tolerance, Float32 metric drift may pass the absolute stretch limit instead.
Every positive-area, winding and ordered-overlap check still runs. This does
not permit independently moving a detail away from its underlay or dropping
tiny source faces. The private model and frozen corpus results must still
validate this resolution-dependent placement policy.

Completed continuation artifacts: `_results~/reverse-model-repair-20261011/texel-policy/`.
Kitchen at 32 tex/m now fills 40.9/45.6% in exclusive/overlap modes, with zero
collapsed faces or unexpected overlaps; Cafe retains its accepted result.
Kamaz overlap fills 50.1%, but exclusive packing remains unacceptable at
34.1% (10.0% at 10 tex/m), and a junction trial still loses inheritance. Kitchen
at 10 tex/m retains a positive-area seed refusal. These are explicit remaining
defects; this continuation does not complete arbitrary-model Transfer quality.

Donor seam precision now reserves one Float32 spacing in the actual seed atlas
rather than eight spacings in the maximum configured capacity. Local emitted
geometry and composed barycentric mappings are audited before accepting cuts.
The nine-asset inherited-area gate passed without relaxing its threshold;
DressingTable recovered above its frozen acceptance threshold. The complete
affected run had 632 passes, 4 skips and one pair-count failure. Subdivision
changed that count while preserving the same overlap area. The frozen Forward
gate now compares overlap area, with a subdivision-invariance control. A focused
repeat passed 206 tests with 8 optional skips and no failures. Both FBX compile
variants passed. See [FORWARD_REVERSE_COMPARISON.md](FORWARD_REVERSE_COMPARISON.md)
for actual UV images, metrics, overlap-coordinate witnesses and limitations.
Main `cd8d848d` / package 1.1.42 is integrated after these captures. The shared
FBX export policy coexists with Binary polygon-split scratch documents and
source-format import probes. Post-merge validation passes 217 FBX tests with
one optional skip, including the four Binary/ASCII input/output combinations;
both reference compile variants pass. These export checks are separate from
the UV-model results above.

Continuation on PR225, baseline `30f2fd94`. The completed Kamaz chain exceeded
the previous rectangle packer's 16-million-work limit and retained the sparse
projection layout in both overlap policies. Final packing now joins explicit
donor ancestry and intentional overlap edges, then reserves each family's
complete footprint across all LODs. Every family receives one rigid quarter
turn, translation and the same global scale; neither LOD0 nor another member is
packed independently.

Disjoint guillotine cells and shelf rows replace overlapping MaxRects cells and
repeated containment pruning. Geometric capacity is searched separately from
Float32 placement validity, which is not monotone in scale. Thin families can
use all four quarter turns and padded fractional origins. A positive origin
phase belongs to its allocated cell; requested two-pixel gutters are preserved
rather than doubled. At the selected scale, shorter canvases in both axes can
reduce large internal empty rectangles without requiring a full square bbox.
Uniform final filling remains optional and records the actual resulting density.

Donor seam selection now finishes before final filling. Comparing two already
filled trials gave the uncut topology an unrelated density bonus and discarded
useful cuts on Wooden_Box_Long. The existing requested-density, inherited-region
and distortion gates still select the topology; one final family pack runs on
the chosen chain. It checks the actual Float32 output vertices and transformed
edges, including barycentrically created corners, with the exported-mesh audit's
arithmetic. Temporary output replacement remains atomic and detached from FBX.

Output rounding is checked against the existing world-space anisotropy limit,
the shared 1% relative metric allowance, normalized triangle area and all
same-LOD contacts. Only explicit ordered detail contacts are allowed. Invalid
placements are retried; failure retains the audited input coordinates. Scratch
intersection predicates for small families match the atlas scan; larger families
retain its grid and completeness/budget checks. The managed final pack runs in
the existing asynchronous path so the Editor can respond to cancellation.

The Viewer formerly rounded a second bbox-normalized Float32 atlas before
intersection checks. This could report contacts absent from the saved UVs.
Normalization now stays in double inside the same predicates. A partial shared
edge control reproduces the former false overlap and still detects a deliberate
intersection. Small nonzero transform scales no longer fail an absolute
determinant cutoff; finite-matrix, genuine singularity and transformed-face
collapse checks remain active.

The final 10 tex/m run passes 583 EditMode cases with four fixture/URP skips.
All 30 ordinary Transfer snapshots for Cafe, Kitchen and Kamaz remain byte
identical to the preceding reviewed baseline. The nine-model Reverse corpus
retains 16 accepted cases and the same two Kitchen seed refusals; inherited
surface coverage improves in every accepted case. Wooden keeps 72 source-face
splits in each policy (previously 73), with higher inherited coverage and no
refinement refusal. Kamaz packs 4,621/4,089 families into 512 square and raises
LOD0 bbox fill from 10.3/10.8% to 39.2/37.3%, with zero unexpected contacts in
both the raw audit and Viewer. Its overlap seam trial still refuses one inherited
region; final packing does not resolve that separate topology limitation.
At 32 tex/m, both completed chains also pass the raw/Viewer audits and the
explicit final-packing assertion: 1024 square, final densities 47.8381/39.7344,
LOD0 bbox fill 52.8/48.3%, and grades 90/89. The two separate LOD0 seam refusals
remain reported; successful packing does not waive them.

Latest main (`f9aad54c`, package 1.1.41) is merged. Its Foliage component lookup
uses the common topology disjoint set, and its preview follows this branch's
manual camera lifecycle. FBX helper refactoring retains PR225's corner-count and
polygon seam-split protection. Measurements and validation for this continuation
are recorded in [KAMAZ_TRANSFER_BENCHMARK.md](KAMAZ_TRANSFER_BENCHMARK.md).

### Texel-sized correspondence tolerance — 2026-10-11

Requested continuation on PR225, baseline `719cce0`. Local inherited metric
comparison permits 1% relative change. Refinement may spend up to 1% of the
density gained by uniform final-atlas filling; the requested minimum remains
audited. Projection and inheritance comparison
try 0.25/0.5 actual texels, rather than a percentage of a potentially very large
face. A failed existing coverage check subtracts the union of donor triangles
dilated by an inscribed octagon: the allowance stays within the Euclidean band
even at acute corners, and duplicate donors never count coverage twice.
Comparison uses the original face's final atlas pixels; barycentric area is
not interpreted as texels. Missing entire inherited faces still fail.

Planar forward recovery converts the same band into distance using the donor
chart's density and the known atlas resolution. With no resolution it retains
the prior exact footprint check. Forward and reverse retain zero-area geometry,
final UV degeneracy, real contact and intentional-overlap ancestry audits.
Clip predicates and Float32 representability preflights are unchanged.
Private fixtures can select the tested band via `MESHLAB_TRANSFER_BOUNDARY_TEXELS`;
runtime code does not read test environment variables.

Final packing fits the completed layout uniformly after integer rectangle
placement. This fills the residual outer strip without decreasing gaps or
changing an individual chart's proportions. Cafe's overlap/10-texel extent
regression is repaired without weakening its existing 0.93 extent assertion.
The restored Tire trial cuts 13 source faces in both policies (previously 6),
at 184.222 tex/unit versus the uncut filled layout's 185.597 (0.74% difference).

The 0.5-texel trial improves Kamaz's forward degeneracy count further, but loses
Park overlap's previously accepted donor cuts. It is rejected; the shipping
default remains 0.25 texel. Neither the cut-retention test nor the UV audits
are relaxed. Independent controls exercise the band in pixel and normalized
units, internal holes, duplicate donors, acute corners, work/cancellation limits,
one-percent metric/density bounds and uniform-fit padding.
Final isolated Unity 6000.2.6f2 replay: 373 cases, 370 passed, 0 failed, 3 optional
skips, including 23 new controls. Both FBX define reference builds, identifier
and dependency gates pass. Nine-model reverse corpus: 16 accepted / 18 trials,
with both Kitchen seed failures retained; Cafe and frozen Kamaz frame 3 pass
both overlap policies. Final Park cuts 251 faces in each policy; Shelf cuts 18.
Forward Kitchen Main LOD1 degenerates: 6 -> 0; Kamaz LOD1/2: 156/104 -> 131/94.
Their contact counts fall from 238/574/99 to 226/510/96. Strong legacy stretch
and Kitchen's Float32 seed face 53 remain unresolved. Captures and complete
metrics: `_results~/transfer-tolerance-20261011/REPORT.md` (local, ignored).
Native plugins, original FBXs and live Unity projects are unchanged.
PR review follow-up: planar recovery uses a scale-independent unit direction
and refuses a zero normal. Millimetre-scale donors retain the same texel-sized
plane test; an off-plane target cannot pass because Unity zeroed the area normal.

### Forward stretch and local donor-seam protection — 2026-10-11

Review continuation on PR225, baseline `2642445`: forward seam trials now use
the target renderer's local-to-world linear transform for every stretch gate,
including donor eligibility, projection, snapped corners, proposed cuts,
per-face rollback, incremental selection and workflow publication. Projection
positions and donor atlas coordinates remain in the existing shared local
frame. Double-precision transformed edges exclude translation. Reverse inputs
already contain world positions, so their additional stretch transform remains
identity; projection reach, overlap policy and tolerances are unchanged.

Two analytic controls use an 8:1 renderer scale: locally isotropic projection
must not replace a visibly isotropic legacy layout with 8x stretch; a locally
8x-stretched donor must remain eligible when it becomes visibly isotropic.
Both fail on the baseline and pass with the rendered metric. Source snapshots
must remain byte-identical. Broader fresh model results are recorded in the
private review replay report.

Review replay: 441/445 EditMode cases pass, zero failures, four optional skips.
Fresh Cafe_Table_A_01 / Modern_Kitchen_A / Kamaz forward outputs match all 30
pre-fix snapshots. Reverse completes 16/18 corpus trials with both policies;
Kitchen retains its two known seed-metric refusals, and difficult Kamaz frame
3 completes with overlap off/on. Final Viewer priority/lifecycle verification
adds 190 passing cases with zero failures. This fixes rendered-space trial
ranking; it does not certify the existing distorted layouts as clean.

Continuation of the transfer work explicitly requested on PR225; baseline
`2820d0a`. A mean Sander L² below 1.5 can hide severely distorted individual
faces. ARAP now also tests local anisotropy above 1.25, on a detached buffer,
and retains only candidates that improve the shell metric without introducing
collapsed faces or UV contacts. Grouped and single-mesh repack share this pass.

A coarse target UV0 shell can span several independently packed donor charts.
Forward recovery uses the existing donor correspondence/seam subdivision on a
working copy. The donor atlas stays fixed. Unsupported regions retain their
previous UVs; projection is rolled back per conflicting piece. Original faces
whose subdivision introduces Float32 contacts or worsens their metric are
protected locally, including incident edges. Final publication still requires
an improved whole-mesh metric and no increase in overlap area. Splitting an
existing contact can increase its triangle-pair count, so contact identity is
audited against original face pairs, rather than judged by that count alone.
Output classifications are rebuilt for the changed topology.

Reverse refinement now protects all affected original faces and donor ancestors
when cuts lose inherited/intentional-overlap coverage or worsen local distortion.
Other seams remain eligible. The inheritance, density and distortion gates are
unchanged; a protected face records `preserved-inheritance-or-metric`.

Final continuation verification: `release5/tests.xml` has 345 passed, zero
failures and five optional skips. `release4` separately verifies all 18 corpus
trials (16 accepted, both Kitchen modes still refused), preserved successful
Shelf/Park cuts, and both policies in the difficult Kamaz frame 3. Only forward
variant selection and additive trace fields changed after those reverse tests.
The full earlier `release2` replay accepts frames 1/2/3 in both modes and refuses
frame 0's real shared-frame geometry collapse. Reference compilation passes
with/without the FBX exporter define; CS0103 and diff checks are clean.

Private replay fixtures: Cafe_Table_A_01, Modern_Kitchen_A and uncompressed
`kamaz_Off.fbx`, plus the frozen Kamaz transform frames. Current forward controls
at 10 tex/m, 2 px padding and junction cuts:

- Cafe source / LOD1 worst anisotropy: 1.45490 / 1.53571, no overlaps or collapsed
  UV faces. Initial LOD2 subdivision retained a severely distorted unsupported
  region (worst 3,465,807.85; weighted 7,914.83). The existing-atlas-only trial
  reduces these to 2,027.82 / 6.02527, with zero collapsed faces or overlaps.
  One unsupported region remains; this is not a complete distortion repair.
- Kamaz main LOD1: 156 collapsed faces / 574 contact pairs, weighted anisotropy
  1,129.43. LOD2: 104 / 99, weighted 527.43. These are partial improvements,
  **not** a clean-transfer certificate.
- Kitchen remains defective: main LOD1 retains 238 contact pairs and six
  collapsed faces. Reverse seed preparation still refuses a positive-area
  Float32 sliver. Origin anchoring and oblique rescue trials did not solve the
  complete model and were removed; the metric gate was not weakened and the
  face was not silently deleted.

Nearly coincident donor rims (a 0.00000012-unit gap in the control) can make one
optional line invalidate earlier valid cuts. Incremental validation retains the
earlier partition and skips the unusable line. Forward recovery compares both
incremental variants with the original method and keeps only an improvement without increased
collapsed-face counts or overlap area. It reuses a fixed donor atlas and retains
legacy UVs for unsupported pieces, so the preflight altitude estimate for future
new charts is inapplicable there. Actual Float32 geometry, mapped UV metrics,
coverage, contacts and final publication guards still apply.
The conservative variant is still needed for coincident rims: dropping its
new-chart preflight globally regressed that control. An unsafe cut at an already
inherited corner retains complete triangles and the prior metric; the control
does not claim that this unsupported region has been repaired.

Replacing reverse refinement with that trial regressed previously successful
Shelf/Park cuts. The replacement was rejected. Reverse now tries the original
method first and retains successful cuts; the incremental variant retries an
accepted layout with no cuts and recorded precision refusals, under the same
chain comparison. Full-chain inheritance/density/metric failures retain the
original result rather than repeat an unsuccessful expensive search.
The corpus benchmark checks that previously successful seam cuts do not all
disappear. Per-piece forward diagnostics record the original face, candidate
metric, projection rejection and the adjacent face responsible for a precision
refusal. Snapping cut intersections to nearby vertices did not repair the real
Cafe case and was removed.

Regression controls include hidden local stretch, planar donor coverage/holes,
cross-chart target subdivision, collision rollback, immutable input snapshots,
both reverse overlap policies and forward-after-reverse reproducibility. Private
model tests assert numeric bounds, in addition to writing snapshots and quality
reports. These fixtures omit external material textures; this run does not certify
texture-aspect resolution through those materials. Artifacts are under
`_results~/transfer-quality-20261011/`; native binaries and live projects were not
modified.

Initial local verification: 342 EditMode passes, zero failures, four optional
fixture skips; reference compilation passes with and without FBX Exporter.
The seven-asset reverse control set retains 12/14 accepted runs; both refusals
remain Kitchen. Frozen Kamaz frames 1/2/3 accept both overlap policies, with
frame 0 still refusing an actual shared-frame geometric collapse. Kamaz overlap
can still retain the unsplit baseline when donor seams lose inheritance.
An additional whole-node ancestry retry did not remove that real-model refusal
and was discarded. No clean-UV claim is made for these accepted but unsplit runs.

### Metric-aware rescue vacancies — 2026-10-11

Continuation on PR225, baseline `5ffec80`. The supplied Kamaz log refuses a
new LOD1 face after both original and compact seed preparation. Frozen 512 /
10 texels-per-unit controls reproduce the same failure class: a positive-area
triangle with a 0.24-micrometre edge gets identical Float32 UV corners when
translated to high atlas coordinates. Reconstructing inside its collapsed
zero-width footprint cannot recover the metric; changing only the stretch gate
would publish invalid UVs.

Independent intrinsic rescues now participate in vacancy selection with their
source metric, rather than their already rounded UV bounds. Their longest-edge
frame defines the reserved rectangle, with at least one texel per dimension.
The existing placement and stabilization are tried first: every already valid
rescue layout is retained exactly, including its original Float32 phase spacing.
The alternative is used only if that layout cannot fit or a rescue loses its
metric. In that alternative the thinnest rescues are placed first. Cyclic bases, quarter-turns and bounded
Float32 translation phases are tested before reserving a vacancy; a failed
position is skipped, the transposed rectangle is tried, and the existing canvas
growth/size limits still apply. Rounded coincident UV edges cannot weld separate
rescues back together. Ordinary charts keep their rigid layout; inherited texel
coordinates, source geometry and the nominal density remain fixed. Publication
and independent overlap/stretch audits stay mandatory. An unrepresentable
triangle remains a refusal, including when no valid vacancy exists.

The frozen Kamaz harness supports explicit resolution/density, a single frame,
the actual junction/packing workflow, and required accepted frames. Source mesh
snapshots are compared after both policies. Its timeout covers successful full
chains rather than only fast refusals. Public controls include the two captured
thin triangles, occupied donor space, padding zero/two, both overlap policies,
final square normalization, exact configured metric-limit acceptance, unchanged
ordinary chart scale and refusal of an unrepresentable triangle.

Isolated Unity 6000.2.6f2: **118 passed, zero failed, nine optional corpus skips**
(365.4 seconds). The 512 / 10 texels-per-unit frozen Kamaz matrix changes from
eight refusals to six accepted chains: frames 1, 2 and the imported identity
frame 3 pass both policies, including independent final metric/overlap audits.
Exclusive results use 1024-square atlases; allowed-overlap results use 512.
Worst anisotropy ranges from 3.94729 to 3.99703; finest inherited area ranges from
90.824% to 92.475%; all six have zero unexpected overlaps/invalid/degenerate UVs.
Frame 0 retains its two earlier refusals because face 9057 collapses in the
shared *3D* frame, before new chart creation. The UV fix does not repair that
different defect. Low triangle-area atlas utilization and declined donor seam
refinement remain limitations. Both FBX-define reference builds and the
identifier/dependency checks pass.

The retained-layout follow-up completes **121 Unity tests, zero failures, seven
optional corpus skips** (521.3 seconds). The actual original/compact seed workflow
accepts the imported Kamaz frame in both policies at 10 texels/unit. All 18 policy
trials on nine FBX chains retain acceptance and atlas size against the current
`8740d15` seed/junction baseline: 16 accepted, the same two Kitchen seed refusals,
zero unexpected overlaps and no decrease in finest inherited area. Wooden's
per-mesh quality measurements are identical to that baseline; its independent
32-texels/unit standalone control passes both policies. The older `30fcd06`
Wooden report predates the finer-inheritance protection and is not the baseline
for this change. Original mesh snapshots remain identical. Both FBX-define
reference builds and identifiers/dependencies pass. Large reports are retained
in the E project's `BenchmarkReports/ReverseUV/codex-4c41a53-precision-20261011`
after the C drive filled during an earlier, discarded corpus attempt.

### Coarsest normalization, compact packing and junction paths — 2026-10-10

Continuation on PR225, baseline `30fcd06`, requested by the user. The forward
solvers/native ABI are unchanged. Narrow cuts previously required one direct
interior edge between two border vertices; extra tessellation prevented the
same geometric cut. A bounded shortest interior-edge path now crosses the join,
stopping at the opposite border. Width remains at most half a straight border
run, path detour at most 1.25 times its chord, and total oriented-box area must
improve by at least 15%. U/T/F/E/H/O/X/C controls cover rotated shapes with three
segments per original grid edge, complete overlap scans and unchanged metric.
Convex, nonmanifold and ambiguous charts retain the existing topology. No new
edge through a face interior is synthesized by this preparation pass.

Reverse preparation now normalizes world density per connected coarsest chart,
repairs invalid UV faces, tests compact rectangle packing with optional quarter
turns/axis alignment, and stabilizes intrinsic rescues at their final location.
Manual mode searches the highest uniform scale that fits; auto mode retains the
requested density. It never scales U and V independently. Incremental pruning
checks only new free-rectangle fragments, avoiding repeated scans of unchanged
pairs on fragmented Train inputs without increasing the packing work budget.

Initial trials were **not safe to publish**: blindly moving the seed reduced
DressingTable inheritance from 96.34% to 88.29%, and some placements failed the
independent final metric gate. Recentring already normalized tiny charts added
Float32 errors. Charts already within 0.1% of the requested world density, or
within the area uncertainty derived from Float32 coordinate spacing and triangle
perimeter, now retain their coordinates exactly. An analytic thin-chart control
checks exact preservation, including already repaired triangles. The normalized
original trial also retains its original canvas when a correction shrinks a
chart, avoiding a fresh global refit of unaffected coordinates. This is a density
normalization tolerance;
zero-area, stretch and overlap publication gates are unchanged.

Train exposed a separate one-ULP median-density change after valid per-chart
corrections: the inferred new-chart density changed from 1.923953533 to
1.923953652 and overlap inheritance decreased by 0.052343 percentage points.
The original-placement trial now carries the original seed's reference median
through projection/refinement, rather than re-estimating it after corrections.
The compact trial still measures its own seed. Frozen prior-commit trial reports
are accepted as the private benchmark baseline so this small correspondence
regression is detected independently of the current legacy solver.

The workflow compares complete chains from the normalized original placement,
compact packing and optional junction cuts. A candidate must preserve every
inherited barycentric region/overlap, density, distortion and atlas side; final
output meshes also pass the independent quality measurement used by the audit.
Failed candidates release their meshes and retain the accepted normalized
control. This may run two or three complete chains. Reports record `seedPacking`,
`seedChartRotation` and `seedPackingRefusal`; the viewport summary identifies a
retained safer layout. Frozen FBX and captured-case benchmarks use this same
workflow rather than bypassing seed selection.

Forward Transfer-only continues to inherit an existing atlas; enable the narrow
cut option before Repack/Full Pipeline. Reverse prepares the last included LOD
before projection and does not require a preceding forward transfer. Once the
seed has been accepted, vacancy placement still only translates new islands;
inherited texel coordinates remain fixed. Remaining Kitchen/Kamaz precision
refusals are recorded separately from passing unit or corpus tests.

Final isolated Unity 6000.2.6f2 validation: **179 passed, zero failed, one unrelated
optional legacy-input skip**, 533.6 seconds. With cuts enabled, all 18 current
solver FBX/policy trials match `30fcd06` in acceptance, atlas size, finest inherited
area, per-mesh quality and density: 16 accepted and two Kitchen refusals. Train's
only remaining measurement differences are triangle UV-area fractions below
2.7e-10 after normalization; inheritance and overlap counts are unchanged. The
eight Kamaz controls still refuse invalid Float32 seed placements. Both FBX
define reference builds, identifier/dependency checks and diff whitespace pass.
The earlier 36-row legacy/current run also retained acceptance and atlas sizes;
superseded packing prototypes are not counted as final validation. Wooden's
compact auto-density candidate is still rejected for lost inheritance, retaining
the 512-square layout and its low 7.34–8.37% triangle UV coverage.

### Final-placement intrinsic precision — 2026-10-10

Continuation of the reverse-atlas experiment on PR225, baseline `0596960`.
Both supplied Wooden_Box_Long logs are identical and load the older `0a544f5`.
Standalone reproduction on the PRE-PROD FBX at 32 texels/unit still refuses
LOD0 face 2 with anisotropy 6.4708. Its nonzero geometry is almost collinear;
normal Float32 shelf and atlas translations introduce shear even after an exact
intrinsic unwrap. Choosing only a different local base edge is insufficient.

Only a failed *independent intrinsic fallback* is reconstructed after its final
vacancy placement. Three cyclic bases, their quarter-turns and sixteen bounded
translation phases are evaluated in double before final Float32 storage. The
candidate must fit the already reserved integer texel rectangle and satisfy the
unchanged anisotropy gate. Ordinary valid charts, inherited UVs, source positions,
face count, nominal density, atlas reservations and publication audits remain.
Cancellation is supported. If no representable candidate passes, retain the
failed UV for the existing publication refusal; never delete a thin source face.

Wooden_Box_Long now passes both current overlap policies at 32 texels/unit:
512-square seed/final atlas, 95.320% finest inherited area, worst anisotropy 1.741,
no invalid/degenerate UVs or unexpected overlaps. Overlap mode classifies three
intentional LOD1 pairs. Manual 256 mode also passes with vacancy packing, finest
inherited area 95.369% / 95.366%; the legacy append-only control remains refused.
A nanometre Backsplash local fixture also becomes valid and now tests independent
quality, preserved source bytes and exact output geometry instead of demanding
its former numerical refusal. This is not acceptance of the complete Kitchen
model: its seed still fails. The eight Kamaz frame/policy trials remain refused.

Packing remains unresolved on this asset: only 7.34–8.37% of the density-driven
512 square is occupied by UV triangle area on each LOD. The repair addresses
metric representability, not seed-chart compactness. Forward's 100% coverage in
the supplied log only counted matched vertices despite degenerate target UVs;
its diagnostics now explicitly say vertex coverage.

The private standalone test is enabled by `MESHLAB_REVERSE_WOODEN_BOX`; outputs
use `MESHLAB_REVERSE_OUTPUT`. Public controls cover cyclic orders, the reserved
footprint, idempotence, ordinary/inherited coordinate preservation, cancellation,
and an unrepresentable placement that stays refused.

Final isolated Unity 6000.2.6f2 selection: **166 passed, zero failed, one optional
legacy-capture skip**. All 32 previous FBX policy/algorithm trials retain identical
acceptance, atlas size, inherited area, complete quality arrays and density extrema;
all eight Kamaz refusals retain their causes. The final ten focused precision cases
also pass. Both FBX define reference builds, identifiers and dependencies pass.
Inputs were copied to the isolated project; FBX SHA-256 remains
`6FA71BC2A815BED6A23AB42AB4B2CC84BA55F73CC18A3F0D9F978E31CC9AE09F`.

### Correspondence graph, donor seam refinement and vacancy packing — 2026-10-10

Continuation of PR225 from `0a544f5`, requested by the user. Reverse remains a
standalone coarse-to-fine alternative; the Full Pipeline still runs forward.
No native solver or imported FBX is changed.

- Geometry edge adjacency records continuous UV, chart/UV seams, layer boundaries,
  open rims and nonmanifold incidence. Connected groups are distinct from root
  donor chart IDs. Source-face maps and barycentric partitions survive subdivision.
- Failed target faces are cut by locally overlapping donor seams/rims in the
  target plane. Subdivision points propagate to incident target faces. Original
  corners keep their bytes; new samples interpolate vertex streams, skin weights
  and blend-shape deltas, preserving submesh slots and packed UV dimensions/formats.
  Position samples use Float32. Optional cuts below representable precision are
  refused locally; thin nonzero source triangles are never removed to pass a gate.
- An initial Cafe/Park trial increased the inherited face count but lost inherited
  *area* on the following LOD. That is a real regression. The final trial compares
  complete regions in original-face barycentric space across the whole chain,
  protects responsible intermediate donor faces, retries local exclusions, and
  retains the continuous baseline if inheritance, overlaps, distortion, density
  or atlas size regresses. Existing accepted claims precede new refinement claims.
- New connected islands use a row-interval occupancy raster across every earlier
  LOD and accepted current faces. Search holes and other vacant texels before
  increasing the square; all chart moves are translations with pixel padding.
  Inherited pixels remain fixed. Grouping or an atlas-size reduction does not
  by itself prove improved inheritance; independent audits report area fractions.

Frozen asset-frame chains, legacy algorithm versus guarded refinement plus
vacancy packing; **both overlap policies**:

| Model | Atlas before → after | Finest inherited area (exclusive / overlap) |
|---|---|---|
| Modern_DressingTable_A | 512 → 512 | 96.338% / 96.338%, preserved by guard |
| speaker-retro | 512 → 256 | 98.514% / 99.126%, preserved by guard |
| Park_Bench_A | 1024 → 512 | 83.337 → 83.364% / 83.265 → 83.324% |
| Shelf_C | 512 → 512 | 99.132% / 99.138%, preserved by guard |
| Tire_C | 1024 → 512 | 72.890% / 72.890%, preserved by guard |
| Bench_Metal_A | 1024 → 512 | 98.062 → 98.107%, both policies |
| TrainCarriage | 1024 → 512 | 94.256 → 94.291% / 94.110 → 94.235% |
| Modern_Kitchen_A | refused in both versions | prepared Backsplash seed loses its UV metric at float precision |

Cafe_Table uses the previously captured exact scene frame. Atlas 256 → 128;
finest UV area fraction 5.76 → 23.02% in both policies at unchanged pixel density.
LOD1 exclusive inherited area 92.900 → 92.930%; LOD0 stays 100%. The overlap
refinement trial retains its uncut baseline after a distortion regression;
vacancy packing still halves that atlas. Geometry/snapshot and final overlap,
degenerate, bounds and stretch audits pass. Fixed density, padding and fine-only
detail reservations do not imply 100% occupied UV area on every coarse LOD.

Kamaz: all eight frame/policy captures remain refused, with exact causes recorded:
one shared-frame collapse and microscopic new-face UV placement collapse in the
remaining frames. These are remaining precision limits, not successful transfers.
Older merged captures with no unique source transform are unsuitable as a live
acceptance baseline; the full imported chains above provide explicit frames.

Isolated Unity 6000.2.6f2 run: 157 tests passed, zero failed, one unrelated optional
legacy-capture test skipped. After Sonar fixes, the focused reverse/junction run
passes 98 tests with zero failures and the same optional skip; all 32 asset
trials retain identical acceptance, atlas size and inherited-area measurements.
Both reference-assembly compile variants and the
identifier/dependency gates pass. Private chains are enabled with
`MESHLAB_REVERSE_ASSET_PATHS` (semicolon-separated imported FBX paths);
`MESHLAB_REVERSE_OUTPUT` retains the old/new per-policy audits and refusal matrix.
`MESHLAB_JUNCTION_FIXTURES`/`MESHLAB_JUNCTION_OUTPUT` retain the Cafe UV/index layouts.

### Narrow UV junction cuts — 2026-10-10

#### Transfer regression and complete-chain packing — 2026-10-11

Continuation on PR225 after the supplied Cafe_Table_A_01 failure at `455a803d`.
Reverse publication assigned the coarsest LOD to the ordinary workflow's source
selector. A subsequent Full Pipeline therefore ran the forward shell matcher
coarse-to-fine. Frozen LOD2-to-fine controls reproduce 24 collapsed faces and
208/213 overlap pairs. Reverse now keeps the forward source selection; display
and export resolve reverse meshes through their provenance. Transfer-only cannot
silently use the original UV2 when that forward source owns only a reverse
transferred result. Full Pipeline rebuilds its original forward inputs. The
complete forward → reverse → forward fixture checks successful completion and
byte-identical fresh forward results with junction cuts on and off.

The previous seed trial rejected any atlas growth and required the new seed
topology to retain every correspondence from the uncut seed. Consequently, tests
could pass by publishing the uncut layout even with cuts enabled. Seed topology
is now selected before projection, and newly unmatched pieces enter the existing
new-chart path. The requested compact/cut seed must pass metric and independent
atlas audits; failures retain an audited fallback with a reason. Within a built
chain, donor refinement still checks each previously inherited region, intentional
overlap and per-face metric. Canvas growth alone is allowed; missing coverage is
still refused. Forward `GroupedShellTransfer`, symmetry splitting and native
`XatlasRepack` are unchanged.

The completed chain has one final rectangle pack by shared chart identity.
Intentional overlay families move together. Every ancestor and descendant gets
the same rigid transform; geometry and UV0 stay unchanged. Quarter turns, three
deterministic rectangle orders and a common-scale search operate on the entire
chain, with fixed pixel padding. `Fill final atlas uniformly` optionally raises
the common density above the requested minimum; the summary/provenance records
the actual density. Disabling it keeps density fixed. A Float32 metric failure
retains the validated coordinates and reports why final packing was skipped.

PR review also exposed a global positional weld in seed preparation. Its key is
now scoped to the input node, matching the already scoped new-chart path. A
coincident two-renderer/shared-Mesh fixture requires separate padded seed islands
and byte-identical source data. Benchmark text now labels schema-2 source quality
with its captured world transform, with the legacy local-space fallback stated.

Overlap validation also used a boolean flag and overwrote a single parent when
one detail intersected several lower faces. The same-level graph now retains
every audited contact to the same donor chart, rebuilding links after seam
refinement. The legacy representative pointer names the highest parent layer;
the serialized overlap list contains all direct edges. Final packing consumes
that graph, and the independent final audit requires the exact recorded pair,
matching donor chart and strictly ordered layers. Stacked details and missing
edge/wrong-chart controls cover this distinction.

Cafe_Table_A_01 at the supplied scene frame, density 10, padding 2: the seed
actually splits 32 → 48 charts. With filling enabled the final 128² chain covers
about 29% of the fine atlas in exclusive mode and 31% with projected overlap,
instead of the retained layout's approximately 9%. Density becomes approximately
36/37 texels per world unit. All three LODs have zero unexpected overlaps,
collapsed UV faces and out-of-bounds vertices. Density 32 and both overlap policies
also pass. Assertions require actual extra seed charts, successful final packing,
atlas extent usage and independent final audits, rather than accepting an uncut
fallback as a cutting success. Synthetic controls preserve exact inherited and
intentional-overlay correspondence with filling on/off. A separate frozen
vacancy control uses one unchanged seed for both projection policies.

The nine-model corpus completes all 18 overlap-policy trials: 16 independently
audited successes and the same two Modern_Kitchen_A seed Float32 refusals. Inputs
remain byte-identical. This is acceptance/UV-safety evidence, not a claim that
every inheritance fraction or atlas dimension is unchanged: changing unpublished
seed cuts can turn previously projected pieces into independently packed charts.
The ordinary solver's older distortion/degenerate-geometry issues are not solved
by fixing its source selection. Private reports and layout PNGs remain outside
the package under `_results~/transfer-regression-20261011/`.

#### Initial trial — 2026-10-10

Continuation of PR225 at the user's request, baseline `16462a0`. The legacy
solvers are unchanged. An opt-in preparation pass cuts existing interior edges
from reflex UV border corners across short junctions. Equal-length proposals
prefer two reflex corners, preventing a T/H junction from acquiring an extra
diagonal cut. Accept only partitions whose summed oriented bounding boxes use
less than 85% of the original box. Convex charts, ambiguous winding and
nonmanifold borders retain their existing topology; bounded optional searches
never refuse the input mesh. Work copies duplicate seam vertices and preserve
the source positions, material triangles and raw non-UV2 corner streams.

- T/H/U/L and rectangular-frame fixtures yield 2/3/3/2/4 strips; straight strips,
  squares and rectangles remain intact. Rotation, scale, reflection, cancellation,
  corner attributes and actual xatlas packing are checked independently.
- Frozen WindowsNA_R_1.5: 20 cut edges, 52 → 66 packed charts, native oversampled
  square side 881 → 815; zero overlap/degenerate faces before and after.
  Glass remains unchanged. The original imported mesh is byte-identical.
- Cafe_Table authored UV0 is already fragmented and remains unchanged by Repack.
  The geometry-derived reverse seed contains the junction pattern. An initial
  asset-frame trial lost seven inherited faces after cutting this seed. This is
  an actual regression from incompatible donor seams, not a packing success.
- Reverse now builds both variants on detached inputs when the option is enabled.
  Compare inheritance per face, intentional-overlap retention, per-face
  anisotropy, density and atlas side. Reject a worsening candidate and publish
  only the baseline, with a visible reason. It can cost two complete builds.
- Replayed Cafe_Table scene transform `(33.65, .002, 51.18)`, Y=89.99995 degrees,
  scale=.84974 reproduces the submitted exclusive-policy report exactly:
  256², 438 inherited, 72 new, 44 ambiguous. Seed junction cuts are refused at
  LOD1 face 83; the published result retains these baseline measurements.
  Projected-overlap policy also retains its baseline: 462 inherited, 48 new,
  20 ambiguous. Independent final audits certify both policies.
- Unity 6000.2.6f2 isolated EditMode selection: 144 passed, zero failed,
  two unrelated private Reverse fixture checks skipped. Includes both junction
  policies, input precision, real xatlas packing, Reverse and FBX export guards.
  Reference compilation passes both FBX define variants; identifier/dependency
  checks pass.
- Repack/private captures exclude collision meshes. No seam is synthesized
  through a face interior. New-block packing still does not search vacant
  inherited atlas space. Detailed-triangle clipping at donor seams remains the
  prerequisite for applying the complete pattern to the reverse Cafe_Table.

Private fixtures: set `MESHLAB_JUNCTION_FIXTURES=Assets/JunctionFixtures`, containing
the original Cafe_Table_A_01 and WindowsNA_R_1.5 FBX/meta pairs, and set
`MESHLAB_JUNCTION_OUTPUT` to an ignored report directory. Run `UvJunctionCutsTests`
in an isolated Editor; Repack reports include both UV/index arrays for visual
comparison and Reverse audits cover exclusive and projected-overlap policies.

### Initial prototype — 2026-10-09

Continuation of PR225 at user request, baseline `67ff4b1`. Isolated new mode;
`GroupedShellTransfer`, `XatlasRepack` and `SymmetrySplitShells` solvers are unchanged.
See [REVERSE_UV_TRANSFER.md](REVERSE_UV_TRANSFER.md).

- Coarsest geometric seed, projection onto the preceding LOD, frozen pixel
  placements, append-only new regions and common final normalization.
- Optional inherited overlaps retain ordered face relationships for future baking;
  no bake-order integration in this experiment. Complete footprint/overlap and
  distortion checks reject ambiguous or unrepresentable results.
- Local intrinsic rescue preserves triangle proportions when connected unwrap
  fails, at the cost of more islands. Sidecars preserve result UV2 and ancestry.
- Unity 6000.2.6f2 DX11: **215/215 EditMode checks passed**, including preview,
  export and capture regressions. Compile check passes both FBX define variants.
- Frozen saved variants from the Modern and Park_Bench captures: **86/112 trials
  accepted**, **26 refused** (10 degenerate input/seed geometry, 8 seed distortion,
  8 collapsed local rescue faces after placement). These are repeated saved
  variants, not 112 distinct models; failures are not certified as successful.
  Audits report no unexpected positive-area overlap in accepted outputs.
- Current limits: incompatible donor seam crossings create new UV instead of
  clipped inherited pieces; intrinsic rescue fragments islands; no global compact
  packing, guaranteed uniform per-face density or production overlap bake queue.

### Reverse rebuild: repair whole shells before correspondence — 2026-10-12

Continuation of the PR225 experiment above, replacing the rejected per-face input
repair and packing trials. The previous implementation is preserved losslessly in
`_results~/reverse-rebuild-20261012/rejected-implementation.zip` together with the
working diff, HEAD and file hashes. Unrelated main, remesh and viewer changes are
retained.

The old input repair parameterized rejected faces independently. A folded quad
became two independently packed triangles, and physically subtexel source faces
could not survive Float32 placement under the same numerical distortion check as
visible faces. Repair now operates on the entire original connected UV component:
shared-corner intrinsic repair, a checked whole flat-patch projection, connected
ARAP relaxation, then native xatlas parameterization of only that failed component.
There is no isolated-triangle fallback. Native repair may introduce required seams
on an already invalid non-planar component; healthy prepared components retain
one similarity transform and their UV adjacency.

Repairs with a unique healthy anchor stay in the ordinary common UV frame. Other
repairs get independent local coordinate domains and immediately become valid
projection donors. Descendants adopt the donor domain only when the complete shell
fits and passes coverage, metric and same-LOD overlap arbitration. Numerically equal
coordinates in unrelated local domains are not a correspondence. Each domain is
audited before packing; every published LOD is additionally audited together in the
final common Float32 atlas. This prevents a repaired coarsest LOD from remaining an
invalid donor until the final packing step.

Whole-shell inheritance runs in BOTH overlap modes. Previously the overlap switch
also disabled ordinary whole-shell movement onto a donor. The switch now governs
only same-LOD projected detail overlap, requiring the actual ordered underlay
relationship. Failed movement restores the complete original shell. The default
prepared path neither refines geometric triangles nor unwraps an unmatched face
subset; exact zero-area cleanup remains restricted to detached working copies.

Final packing starts from an empty canvas and compares complete classic native and
managed placements of all linked shell families. Families share one rigid motion
and common density across LODs, including intentional overlays. Packing-mask union,
triangulation and short connectors are private proxy geometry only. A proxy whose
Float32 connectors collapse uses a conservative envelope for that family rather
than an unbounded connector width; the latter caused a reproducible native crash
on Kamaz in a rejected trial. No native plugin binary or source is changed here.

Genuinely resolution-limited faces may spend at most 0.01 bake texel on a shared UV
vertex to avoid Float32 collapse. Incident faces and linked LOD corners move
together. Visible distorted faces cannot use this exception, and the bound cannot
be exceeded even at an unrepresentable large atlas offset. Counts are exposed in
`resolutionLimitedFaces`; raw worst-stretch diagnostics remain visible.

Validation of the rebuild uses Unity 6000.2.6f2 in the isolated frozen-fixture
project. 447 EditMode tests passed, zero failures; 34 optional fixture cases
skipped. Both compile-check FBX define variants passed. The new cases exercise
folded shared quads, a wholly zero-UV repaired seed used as the actual next-LOD
donor, whole-shell inheritance in both overlap policies, shared-corner precision,
and ignored microscopic native proxy connectors. Paired model results and actual
UV comparisons are retained under `_results~/reverse-rebuild-20261012/models-4/`.

Review follow-up: snapshot payloads now preflight aggregate channels and all
submesh indices before allocating one bounded buffer; Unicode names retain the
version-1 binary format. Tests cover both oversized aggregates and round trips.
CI findings in the rebuild were fixed without changing packing tolerances or
ordering. The repeated 447-case suite and both FBX compile variants passed.

Three further confirmed review findings are covered by regressions: authored
shell overlap is measured after uniform normalization (including 1e-9 and 1e9
scales), interpolated new-corner normals and tangents form an orthonormal frame
while original bytes/handedness survive, and hole inspection reads an owned
Editor snapshot for Read/Write-disabled meshes and disposes it before background
work. No importer or original mesh is modified by these inspections.

All six full model runs completed, and all sixteen reverse LOD audits contain zero
invalid/collapsed faces and unexpected overlaps. All thirty ordinary Forward layout
JSON files are byte-identical to the previous published control. Source snapshots,
face partitions and geometric triangle coordinates are unchanged (apart from the
previously authorized exact-zero-area cleanup on working copies). `splitSourceFaces`
is zero in every reverse chain. A separate NumPy audit checked adjacency and one
similarity for every healthy original shell: 319 Cafe, 1244 Kitchen and 4989 Kamaz
shells per mode, with maximum normalized-coordinate residual 1.59e-7.

LOD0 occupied-bbox fill (ordinary / reverse exclusive / reverse overlap):

| Fixture | Ordinary | Exclusive | Overlap |
| --- | ---: | ---: | ---: |
| Cafe_Table_A_01 | 40.2% | 80.4% | 80.4% |
| Modern_Kitchen_A | 39.4% | 62.5% | 62.4% |
| Kamaz Typhoon | 57.5% | 74.8% | 74.9% |

P95 stretch remains 1.210 / 1.101 / 1.064 on those LOD0 results. Whole-component
repair touches 16 / 760 / 1276 faces respectively; these counts include healthy
incident faces kept connected to a repaired vertex, not only the initial defect
faces. Genuinely resolution-limited faces: 0 / 53 / 10 per chain. Overlap mode keeps
731 audited Kitchen LOD0 underlay contacts and 417 Kamaz LOD0 + 174 LOD1 contacts;
exclusive mode keeps none.

Limits are not hidden by passing defect tests. Cross-LOD UV alignment is still
73.3-73.5% on Kitchen LOD1, 87.7-88.4% on Kamaz LOD1 and approximately 83.3% on
Kamaz LOD2. Cafe LOD1 improves from 65.5% in the old exclusive path to 99.2%; its
coarsest LOD stays at 45.8%. Packing is not optimal: the rejected per-face repair
trial reached 78.4% Kamaz overlap fill by fragmenting defective shells; this intact
rebuild reaches 74.9%. Invalid non-planar input components may acquire native
parameterization seams, while healthy components remain unchanged. The raw worst
stretch of the 0.01-texel precision faces can still depress the unified quality
score, despite negligible world area; the raw diagnostics were not suppressed.

### Reverse regrouping of ordinary Transfer charts — 2026-10-12

Continuation of PR225, requested after the fragmented reverse results. The default
Reverse action now prepares the same ordinary Analyze/Weld/Symmetry/Repack/Transfer
chain on detached working entries. Repack performs the configured T/H/L/O and other
junction cuts once. Imported precision preparation precedes the detached copies;
the source FBX geometry is not overwritten. The original forward source selection
is retained. Disabling preparation consumes an already completed shared forward
atlas, and refuses independent per-mesh atlases or previous reverse provenance.

The source of the fragmentation was per-face acceptance followed by unwrapping and
cutting the rejected subset. The new path retains the prepared island even when
only some of its faces obtain correspondence. Existing matching UV coordinates are
kept; projected detail may move only as a complete fitted island, and every face
must pass coverage, metric and ordered-contact checks. A failed proposal restores
the complete island. Final packing unions each prepared island with its validated
LOD ancestry and overlays, then repacks all families on an empty canvas. No donor
seam refinement or unmatched-face junction cutting runs in this path.

Actual input defects are a separate exception: collapsed/stretched faces and
measured UV contacts in the ordinary result are repaired before grouping. These
repairs are explicitly logged and retain source-face identity; they are not
triggered by ambiguous projection. This does not claim that every authored island
can be kept intact when its input UVs already overlap or collapse. Strict overlap
and absolute distortion audits remain in place. Unity Unwrapping is not used.

The paired benchmark additionally checks that detached preparation is byte-identical
to the ordinary pipeline, including vertex streams and topology, and retains the
actual forward atlas dimensions instead of assuming 256 pixels. It now fails if
any requested reverse mode fails, while still saving the comparison JSON.

Validation for this continuation (Unity 6000.2.6f2, private frozen fixtures):

- 363 individual EditMode tests passed across the regression suite and the focused
  preview rerun; 34 optional fixture cases skipped. Both compile-check FBX define
  variants pass; metadata and whitespace checks pass.
- Cafe_Table_A_01, Modern_Kitchen_A and kamaz_Off completed both overlap policies
  from ordinary prepared inputs, at a requested minimum of 32 tex/m and 2 px padding.
  All 16 reverse LOD quality reports have zero collapsed faces, invalid faces and
  unexpected overlaps. `splitSourceFaces` is zero in all six chains.
- Similarity fitting against ordinary output checked 319 Cafe, 1238 Kitchen and
  4989 Kamaz healthy islands in each policy. Maximum UV residual was 1.51e-7;
  islands containing input repairs were explicitly excluded from this assertion.
- Input repairs across the chain: Cafe 14 faces, Kitchen 515, Kamaz 873. This remains
  a separate limitation of the ordinary input rather than a projection-fragment
  repair policy. Original FBX snapshots remained unchanged.
- LOD0 bbox fill, ordinary / exclusive / overlap: Cafe 40.2 / 53.8 / 64.9 percent;
  Kitchen 39.4 / 41.0 / 42.4; Kamaz 57.5 / 56.0 / 54.7. Packing quality on Kamaz
  remains below ordinary Forward and is not claimed solved by this change.
- Intermediate coordinates are scaled together by an exact binary factor before
  correspondence so forward oversampling does not tighten the bake-space tolerance.
  The oversampling regression checks identical final layout at 128 and 1024 input
  dimensions with uniform final filling disabled.

Artifacts: `_results~/reverse-chart-groups-20261012/density-models/` contains all-LOD
and LOD0 UV comparisons, paired JSON audits and `island-preservation.json`.

#### Preserve the shared Forward footprint during final packing — 2026-10-12

The previous graph retained only explicit per-face reverse acceptance. Ordinary
Transfer had already aligned additional islands across LODs, but ambiguous reverse
proposals dropped those links. Final packing then allocated separate space to the
same prepared UV footprint. This was a correspondence loss, not a missing call to
repack. A rectangle-only experiment confirmed that replacing the rectangle search
could not recover that duplicated reservation and was discarded.

For `regroupPreparedAtlas`, final packing now joins positive-area UV intersections
across different LODs of the same `ExtractGroupKey` before building rigid families.
The precondition is the existing shared ordinary Transfer atlas; this rule does
not apply to arbitrary independently unwrapped LODs. Same-LOD overlaps are never
whitelisted by this relation, and projection/overlay provenance is not fabricated.
The prepared island and every explicit ancestry/overlay link remain indivisible.

Large family sets additionally search a 1024-cell occupancy grid at several common
scales. The grid reserves conservative padded footprints; emitted corners are
rebuilt from the original coordinates in double precision and pass the existing
metric/contact audit. The search is not a quantization of UVs. Native precision
rescue now isolates only families that actually fail a placement, instead of all
families below a blanket minimum-altitude threshold. All complete candidate layouts
compete on occupied bbox area divided by their common scale squared.

Regression coverage includes ambiguous reverse proposals with valid shared Forward
footprints in both overlap policies, mesh-group isolation, intact prepared islands,
precision exceptions and existing padding/contact/cancellation checks. Private
fixtures are run through the real ordinary preparation plus reverse workflow; a
post-packing replay alone cannot restore relationships already lost by an old run.

Validation (Unity 6000.2.6f2, frozen private models, 32 tex/m, 2 px padding):

- 368 EditMode tests passed, zero failures; 34 optional fixture cases skipped.
- All six complete reverse runs and all 16 LOD quality audits completed with zero
  invalid/collapsed faces or unexpected overlap. Input FBX snapshots are unchanged;
  every ordinary Forward layout is identical to the previous benchmark output.
- LOD0 bbox fill, previous exclusive / new exclusive / previous overlap / new
  overlap: Cafe 53.8 / 81.3 / 64.9 / 81.0 percent; Kitchen 41.0 / 61.9 / 42.4 / 62.9;
  Kamaz 56.0 / 72.0 / 54.7 / 78.4.
- 6546 healthy prepared islands were checked independently in each overlap policy.
  Maximum similarity residual is 1.60e-7 normalized UV; no new triangle splits.
  Input repairs (Cafe 14, Kitchen 515, Kamaz 873 faces) remain unchanged and are
  excluded from the healthy-island assertion. They are not disguised as packing.
- Final families exclusive / overlap: Cafe 122 / 122, Kitchen 1243 / 989, Kamaz
  3025 / 2884. P95 stretch remains 1.210 / 1.100 / 1.064 respectively.
- Artifacts: `_results~/reverse-shared-footprints-20261012/models/` contains paired
  audits, all-LOD plots and before/after UV comparisons. Kitchen still has packing
  headroom; the result does not claim an optimal layout.

### Reverse precision and frozen regression gate — 2026-10-09

Continuation of the same experiment at baseline `a72e06f`; legacy solvers remain
unchanged. Double intermediates now begin before geometry/UV edge subtraction,
dot/cross products and intrinsic rescue coordinates. Kitchen Countertop face 38
previously reported different distortion for cyclic vertex order, despite the same
geometry and UV. Six corner-order fixtures and four scale fixtures verify the
metric; the quality threshold remains unchanged.

The final independent audit now refuses excessive stretch after normalization,
even if its positive-area overlap scan is clean. A negative control changes only
final UV2 to verify this gate. The frozen test checks every accepted audit and,
when `MESHLAB_REVERSE_BASELINES` is supplied, requires every previously accepted
pair/policy to stay accepted. Benchmark failures record their processing stage;
degenerate input errors name the face and, for projection inputs, the LOD/key.

- **228/228** local Unity 6000.2.6f2 DX11 EditMode checks pass.
- Same frozen corpus: **86/112 accepted, 26 refused**, no lost accepted variants,
  and no unexpected overlap or incomplete scans in accepted outputs.
- The remaining repeated Kitchen failures are not cured by metric precision:
  Backsplash fine face 22 has altitude **1.2317e-8 m** over a **0.68664 m** edge;
  its local UV collapses after atlas placement. Countertop coarse face 38 has an
  **8.8298e-6 m** altitude, and prepared seed placement still yields distortion
  **32.973**. Exact captured coordinates are regression fixtures. These are 16
  trial refusals across repeated captures/policies; ten further trials contain
  genuinely degenerate geometry (seed faces 8618/96 or detailed face 10925).
- Geometry is not silently removed and the stretch threshold is not relaxed.
  Connected unwrap/placement of very thin faces remains an open limitation.
- A shortest-edge intrinsic basis was tested and rejected: it changed Backsplash
  face 22 from a collapsed line to distortion **13.441**, still above the limit;
  the isolated placement negative control reached **95.587**. It did not recover
  any corpus trial, so that packing change is not included.

### Reverse shared projection frame — 2026-10-09

Continuation of the same PR225 prototype at baseline `33150eb`. Seed preparation
and every projected LOD share a translated world frame. Double matrix products
and translation subtraction happen before float BVH coordinates are stored;
source-local geometry is checked first. The frozen Kamaz triangle fixtures cover
world-position collapse and a genuinely collinear source face. The latter remains
a refusal; no triangles are dropped, thresholds relaxed or legacy solvers changed.
Translation controls compare complete UV output and inherited face counts in both
overlap policies. The previously accepted frozen variants remain **86/112** with
no lost success; all remaining 26 refusals are retained and stage-classified.
The combined local Unity 6000.2.6f2 battery passes **309 checks**, with **7 private
fixture checks skipped** and no failures. It includes the frozen reverse corpus,
Kamaz exact-triangle controls, preview/export/capture and planar/local/bridge
closure regressions. Reference compilation passes both FBX define variants.

> **Обновлять этот документ при каждом эксперименте с transfer pipeline.**
> Последнее обновление: v1.1.34 (2026-10-09)

## Source UV packing: relative face degeneracy — 2026-10-09

- Продолжение PR #225, baseline `24266da`. Изолированный Unity 6000.2.6f2 / DX11 проект, оригинальные FBX и meta; изменения геометрии и UV0 запрещены. Проверяется подготовка source до transfer, включая reverse LOD1→0.
- Stage probe TrainСarriage_Base_LOD0 и Modern_DressingTable_A_Frame_LOD0: после ARAP и density normalization у Frame 0 вырожденных UV-граней, но xatlas оставляет 50/51 orphan vertices (weld off/on). После orphan repair появляются 11/10 вырожденных граней. Train имеет 8 исходных вырожденных 3D-граней; дополнительно xatlas игнорирует 248/280 корректных маленьких UV-граней.
- Причина: `AddUvMesh` сравнивает float UV area с абсолютным `FLT_EPSILON`. Порог зависит от единиц UV, поэтому normalization уменьшает корректные triangles ниже cutoff. Это source packing defect, а не ошибка BVH traversal или source assignment.
- Отклонён общий ×16/×32 scale входа: он убирает orphan collapse, но меняет автоматический расчёт texels-per-unit (`max(1, meshArea / .75)`) и round-up размеров chart. На 174 prepared rows (87 inputs × grouped/grouped-no-hints) 32 строки ухудшили хотя бы одну quality metric, в том числе Kitchen. В production масштабирование не переносится.
- Native bridge компилирует pinned xatlas implementation в своей translation unit, затем после успешного `AddUvMesh` пересчитывает `faceIgnore` до chart construction: finite float coordinates, double determinant, relative cutoff `4 * DBL_EPSILON * max(edgeLengthSquared)`. Unmodified vendor snapshot и C ABI сохранены. Координаты, packing options и texels-per-unit не масштабируются. Коллинеарные и non-finite faces остаются ignored.
- Добавлены native CTest fixtures для маленьких, mirrored, translated, collinear и non-finite UV, плюс EditMode проверки automatic/explicit density и mixed-scale shared atlas. Native binaries обновляет только build-native CI.
- CI rebuild `37910357333`: Windows/Linux/macOS (universal), native uv-input и remesh CTests проходят на всех трёх платформах. В изолированном Unity: **154 passed, 0 failed, 3 ignored** (отсутствующий Cabinet corpus); все три новые native-backed EditMode проверки проходят. Обе FBX reference-сборки, dependency/identifier и meta gates проходят.
- Native stage probe: Train source weld off **43 degenerates / 17822 overlaps → 8 / 0**, weld on **42 / 13346 → 8 / 4**. Остаточные 8 faces вырождены в импортированной 3D-геометрии. Frame weld off **11 / 295 → 0 / 10**, weld on **11 / 194 → 0 / 7**; остаточные folds уже есть после ARAP. Площадь/форма source chart больше не портятся из-за orphan recovery.
- Final prepared corpus **174 rows, 2 repeats**, complete, errors=0, deterministic, inputs unchanged. Geometry, indices, normals, tangents и UV0 не изменены. **32 rows изменены**, остальные 142 byte-identical. Frame target weld off pairs **111→0**, worst stretch **4156.58→8.85**; weld on degenerates **3→1**, pairs **12→0**, worst **3790326.83→54.04**. Shelf/Tire/Park/Bench результаты сохранены.
- Это **не regression-free end-to-end transfer**: строгая проверка семи source/target quality metrics отмечает **26 rows** с хотя бы одним ухудшением. В Kitchen есть float line→sliver / sliver→line tradeoffs, а Train LOD2 weld off degenerates **179→188**, хотя pairs **5321→435**. Train reverse weld on LOD1→0: pairs **3851→140**, degenerates **175→171**, но mean stretch **16.54→31.59**. Native pack исправлен; target projection/legacy candidate selection ещё требуют отдельного исправления.
- Отклонён дополнительный trial выбора transform по локальной quality: 174 rows, 44 strict metric regressions; Train weld off LOD2 pairs **435→438** относительно native-only и ряд Tire numeric metrics хуже. Локальное улучшение chart не сертифицирует cross-chart footprints. `GroupedShellTransfer` оставлен без изменений; этот trial не входит в production.
- Подробный отчёт и точные labels/stage metrics: [SOURCE_UV_PACKING_REPRO.md](SOURCE_UV_PACKING_REPRO.md). Native-only binaries опубликованы штатным CI; остаточные ARAP folds, thin/degenerate authored geometry и недостатки обратного трансфера не считаются исправленными.

## Projection candidate quality and source-atlas gate — 2026-10-09

- Продолжение PR #225, baseline `269bba6`. Сначала разделены два механизма
  Bench LOD2: inherited hint выбирает opposed distant surface вне UV0 interior;
  normal-filtered UV0 queries чистого chart выбрасывают близкие triangles и
  складывают целый target chart на удалённую границу. Brute-force double queries
  независимо воспроизводят исходный результат, поэтому BVH traversal не причина.
- Shared `TransferCandidateQuality` измеряет geometry anisotropy (worst и
  area-weighted), collapse/missing UV, internal overlap, bounds, density/CV.
  Recovery UV0 без normal filter и 3D projection допускаются только для clean
  source atlas, unshared/unrestricted chart без UV0 stacking. Они не меняют source
  assignment и не экстраполируют за source triangles. Candidate comparison
  не допускает больше issues/overlap и требует улучшения stretch. Line → floating
  sliver при прежнем числе collapse issues не считается исправлением.
- Hint guard дополнительно отвергает opposed hint только при >2× distance
  до 3D interiors и отсутствии UV0 interior coverage hint. Correct displaced
  UV0 feature и thin-sheet tests сохранены. Classification происходит после
  topology correction и учитывает stretch >4 и internal folds; trace содержит
  before/final quality, source-atlas gate, rescore/dedup stages и ограниченный
  объём raw recovery candidates (4096 vertices/candidate, 65536/case).
- Отклонён вариант с проверкой лишь отдельного source chart: на bad source
  Train LOD2 pairs 4953→4966, DressingTable Frame overlap area +0.51%, несмотря
  на улучшение локальной candidate quality. В окончательном варианте bounded
  scan всего source UV2 обязан завершиться с 0 overlap/degenerate/invalid/OOB.
  Train и DressingTable Frame сохраняют исходные UV2 bytes. Это консервативный
  guard, а не исправление дефектов source.
- Final frozen corpus: 74 captured pairs + 7 analytic controls × 6 methods =
  **486 rows**, two repeats, row errors=0, deterministic, inputs unchanged.
  **58 UV2 results изменены**, все grouped/grouped-no-hints. При сравнении
  degenerate/invalid/OOB, pairs/area и worst/area-weighted anisotropy ни одной
  регрессии (epsilon 1e-12 для area и 1e-6 для numeric stretch). Пять Train
  nearest-method scans остаются incomplete как и в baseline; grouped scans полные.
- **140/140 affected EditMode tests**, zero failures/skips; обе FBX define
  reference-сборки, identifier/dependency guards и meta/GUID checks проходят.
- Full Bench LOD2: **0/13 → 0/0**, mean anisotropy **477.6345→1.9044**,
  worst **6,418,951.6→148.45**. Controlled Bench LOD2: **0/6→0/0**;
  Packed Tire LOD2: **0/14→0/7**. Shelf reverse square worst **14.19→7.36**,
  1:2 **9.11→4.22**, both still 0/0. Bench LOD1 remains 1/8, and target
  distortion is not fully repaired. DressingTable Handle clamp remains.
- [Подробные измерения](MODERN_TRANSFER_REPRO.md#projection-candidate-quality-and-source-atlas-gate).
  [Присланный joint-layout алгоритм](LOD_UV_ALGORITHM_REVIEW.md) рассмотрен
  отдельно: uniform packing сохраняет aspect distortion, greedy assignment
  проигрывает альтернативе на воспроизводимом fixture, UV0 overlap не доказывает
  cross-LOD correspondence. В production совместная перестройка атласа не перенесена.

## Bench_Metal_A: shared boundaries and inherited chart hints — 2026-10-09

- Продолжение PR #225, baseline `927eff1`. Actual prefab использует FBX GUID
  `7896c81e71a3ef74f804c0e590cb339e`, 1668/1268/272 faces на LOD0/1/2.
  Original FBX/meta скопированы в изолированный Unity 6000.2.6f2 / DX11 проект.
  Controlled preparation: manual 512, square metric, weld on, pre-optimize off,
  symmetry off/legacy/adaptive × ARAP off/on — 12 target cases; source UV2 0/0.
- LOD1: target boundary vertices точно лежат на неправильном coplanar frame,
  но face interiors находятся в его отверстии. Vertex-only average distance
  ≈1e-12 не отличает рамку от заполненной поверхности. Для t47/t150/t161
  UV2 interior coverage неправильного source равен 0%; source bbox скрывает
  проблему. Bounded sampling до 32 face centroids добавлен к каждой candidate
  оценке и dedup retry: score использует max(vertex distance, interior distance).
  Candidate set, normal penalty и merged rescore сохранены.
- Cross-LOD hint path раньше обходил surface matching. Full-pipeline frozen
  LOD2 с hints имел 30 degenerate faces / 111 overlaps; без hints — 0 / 6.
  Geometric-only rejection hints отклонён: Train LOD2 ухудшался 19 → 47
  degenerates. Relative UV0 rejection тоже отклонён (19 → 25). Итоговый guard
  отклоняет hint только при >2× distance до 3D interiors, когда alternative
  покрывает sampled UV0 interiors в пределах squared tolerance 1e-8, а hint
  не покрывает. Это сохраняет UV0 feature для деталей, смещённых decimation.
  Trace сохраняет отдельные optional 3D/UV0 interior distances и причину rejection.
- Controlled Bench LOD1: **8/418 → 2/7**, LOD2: **0/119 → 0/6**, одинаково
  во всех шести settings. Остаточные LOD2 pairs — folds внутри двух charts;
  sampling не является сертификатом полного triangle coverage.
- Frozen Full Pipeline inputs после interior scoring: LOD1 1/8; LOD2 после
  окончательного hint guard **30/111 → 0/13**. Полное отключение hints даёт
  меньше pairs, но отбрасывает полезные feature matches; оно не принято.
- Семь analytic cases: три масштаба общей границы, те же три с неверным hint,
  и сохранение правильного UV0 hint после displacement. Первые три падали
  до correction. **136/136 affected EditMode tests**, zero skips; обе reference
  C# сборки и identifier/dependency guards проходят.
- Corpus tradeoff: Train LOD1 66/11797 → 62/11800; summed pair area растёт
  на ≈0.0115%, source уже имеет 32 degenerates / 13964 pairs. Train grouped
  LOD2 19/4955 → 19/4953. Shelf reverse cases становятся 0/0; Countertop
  degenerates уменьшаются 14→3 без weld и 15→4 с weld. POST-DEDUP warnings
  сохранены; ни Bench, ни Train ещё не имеют полностью чистого target atlas.
  [Измерения и local evidence](MODERN_TRANSFER_REPRO.md#bench_metal_a-shared-boundaries-and-cross-lod-hints).
- Final frozen corpus: 74 captured pairs + 7 analytic controls × 6 methods =
  **486 rows**, two repeats, row errors=0, deterministic, inputs unchanged.
  Пять Train nearest overlap scans остаются lower bounds; Bench scans полные.
  Final async Full Pipeline с Checker: 9839 updates, missing meshes=0,
  repeat TransferAll capture подтверждает LOD1 1/8 и LOD2 0/13.

## Tire_C: tessellation-dependent normal override — 2026-10-09

- Продолжение PR #225, baseline `ebe5fcd`. Actual Tire_C prefab использует
  Packed FBX GUID `f23fd07c97f149d4bac9b04b087dfd44`; Separate — отдельный
  input. Обе версии имеют LOD0/1/2: 816/560/144 faces. Копии original FBX/meta
  импортированы в изолированный Unity 6000.2.6f2 / DX11 проект.
- В controlled preparation source UV2 чистый у обеих версий. Packed LOD1
  исходно имеет 87 degenerate faces / 8727 positive-area overlap pairs;
  Separate LOD1 — 0 / 0. У обеих версий LOD2 остаются 14 overlap pairs.
  Matrix: manual 512, weld on, pre-optimize off, square metric,
  symmetry off/adaptive, ARAP off/on — 16 target cases.
- Match trace Packed LOD1 выбирает внутреннюю поверхность вместо внешней:
  правильный source находится на squared surface distance ≈5.5e-13,
  но unweighted average normals имеют dot=-1. После decimation средняя normal
  меняет знак из-за количества мелких граней. Weighted cross sums сохраняют
  направление: source Z≈-0.3767, target Z≈-0.3586; unweighted sums дают
  Z≈-13.86 и +16.62. Это не opposite side тонкого листа.
- Полная замена всех средних нормалей отклонена: ухудшает Kitchen/Train
  в 402-row comparison. Итоговый guard сохраняет прежнее scoring/rescore,
  но запрещает opposed-normal override для почти точного sampled surface
  match (squared tolerance = mesh diagonal² × 1e-10), если area-weighted
  направления согласованы (dot≥0.3). Nearly closed chart не получает
  выдуманную area-normal. Проверка обратной стороны thin sheets сохраняется.
- После исправления matching остаются folds от normal-filtered interpolation
  внутри правильного source. Только для защищённого match на равном числе
  face issues выбирается существующий transform, если его bounded overlap
  scan полный и чистый, а interpolation не проходит эту проверку.
  POST-DEDUP warning и проверки настоящего shared-source ambiguity сохраняются.
- Четыре analytic retessellation fixtures (subdivision source/target, 8/40
  segments) сначала выбрали remote wall вместо правильного chart. Итоговый
  subset: **129/129 EditMode tests**, без skips. Обе reference C# сборки,
  identifier/dependency checks проходят. [Корпус и результаты](MODERN_TRANSFER_REPRO.md#tire_c-tessellation-dependent-normal-override).
- Final frozen corpus: 60 captured pairs + 7 analytic controls × 6 methods =
  **402 rows**, два повторения, deterministic, inputs unchanged, row errors=0.
  Пять Train nearest scans неполные (lower bounds). Все Park/Modern/Shelf outputs
  совпадают с baseline. Train grouped LOD1: 124/11830 → 66/11797; grouped LOD2
  не меняется. No-hints LOD2: 129/4319 → 120/4327 (не чистый output и не
  production hint path). Пять positive analytic grouped controls проходят.
- После финальной проверки dedup propagation повторены **134 grouped/no-hints
  rows** на всех 67 парах: UV2/mapping hashes и все quality counters совпадают
  с 402-row comparison; ошибки=0, deterministic, inputs unchanged.
- Tire Packed LOD1: **87/8727 → 0/0**, weighted anisotropy 23.139 → 1.283,
  во всех четырёх prepared settings; Separate LOD1 не меняется. LOD2 обеих
  версий сохраняет 14 pairs. По два async Full Pipeline с Checker на LOD1/2
  завершились, missing render meshes=0. Specific src3/t3/t4 POST-DEDUP строка
  в controlled/full probes не воспроизвелась; source-claim warnings не удалены.

## Park_Bench_A: internal Mesh LOD indices и отклонённые matching variants — 2026-10-09

- Продолжение PR #225, baseline `2c40343`. Проверены две разные FBX версии:
  actual prefab использует Packed (LOD0/1/2: 3832/1498/230 faces), Separate
  содержит 9110/2306/1498 faces и внутренние Mesh LOD ranges. Их albedo textures
  квадратные: 4096×4096 и 2048×2048. Original FBX/importer meta не менялись.
- Separate падал при native UV packing после создания working copy. Для LOD0
  MeshData descriptor сообщает 36561 indices, но Mesh.GetIndexCount — 27330.
  MeshData.GetIndices заполняет только 27330: sentinel probe подтвердил 9231
  нетронутый элемент в хвосте. У LOD1/2 хвост содержит 2553/1533 элемента.
  Uninitialized tail попадал в SetIndices. ReadIndices теперь выделяет массив
  по active Mesh.GetIndexCount; обычный и raw-UV copy используют один helper.
  Native binaries, импорт и исходные UV не меняются.
- Два UInt16/UInt32 regression fixtures воспроизвели пустую/невалидную topology
  до исправления. Итоговый subset: **125/125 EditMode tests**, без skips,
  Unity 6000.2.6f2 / DX11. Обе reference C# сборки, identifier/dependency checks
  проходят. По два async Full Pipeline для Packed/Separate с Checker на
  LOD1/2 завершились; missing render meshes = 0 во время updates и после
  смены preview mode.
- Controlled preparation: weld on, pre-optimize off, manual 512, square metric,
  symmetry off/adaptive, ARAP off/on. Separate targets чистые в восьми случаях
  (zero degenerate faces / positive-area overlaps). Это не утверждение о
  неизвестных UI auto-tune settings. Packed остаётся дефектным: с ARAP source
  имеет 0 degenerates / overlaps, но grouped LOD1 — 0 / 4, LOD2 — 5 / 52.
- Проверены и отклонены matching variants: nearest-face normal вместо average
  normal для curved fragment, ограничение merged rescore по 3D distance и
  дополнительный UV0 coverage gate. Analytic curved fixture подтверждает
  ошибочный source override, но на реальном Packed эти изменения увеличивают
  summed overlap area с ≈0.00535 до ≈0.0255; rescore variant также увеличивает
  degenerate faces с 5 до 9. Они **не включены** в итоговый patch. Ни один из
  шести benchmark methods не даёт чистый Packed LOD2. Требуется согласованное
  исправление matching и проекции, а не смена метода по одному счётчику.
- Старые CollapseDiag строки `uv2 aspect vs 3D bbox` не доказывают sliver:
  bbox-проверка уже заменена triangle anisotropy в текущей ветке. Реальные
  overlaps/degenerates подтверждены полным независимым scan. [Данные и
  ограничения](MODERN_TRANSFER_REPRO.md#park_bench_a-internal-mesh-lods-and-packed-quality-limits).
- Итоговый frozen regression corpus: Park 4, Modern 16, Shelf 12, Train 8
  пар × 6 методов = **240 rows**, по два повторения. Все outputs deterministic,
  inputs unchanged; 40 grouped references совпадают. Park hashes/mappings
  совпадают с baseline во всех 24 rows. У пяти Train nearest rows overlap scan
  неполный (lower bounds); ни один Park scan не исчерпал budget.

## TrainCarriage: pack budget и выбор source для instance — 2026-10-09

- Продолжение PR #225, baseline `ca2daf0`. Присланные stack traces относятся
  к `59d634d`, но pack refusal воспроизведён на текущей копии модели: 1428
  source shells, 4096 × oversample 4 → 383B cost, 2048 × 4 → 95B, оба отклонены
  бюджетом 20B. На 2048 × 1 pack проходит примерно за 0.4 s. Native binaries
  и бюджет не менялись. Сварка всех импортированных мешей сохраняет corner
  tangent.w; для корпуса LOD0/1/2 изменённых signs — 0.
- RepackSingle/Multi выбирают максимальный внутренний oversample, помещающийся
  в бюджет; requested resolution и пропорциональный padding сохраняются.
  Если даже factor 1 не помещается (корпус на 4096), возвращается причина
  budget refusal вместо `cancelled`. Full Pipeline проверяет все included
  source outputs: partial/all failed Repack останавливает transfer/auto-tune,
  не пишет успешный capture/benchmark и не сообщает Complete. Успешные source
  outputs остаются доступными. User cancellation сохраняет свой отдельный путь.
- `ElectricWagon_Seat_LOD1.001/.002` не имели одинакового group key со своим
  LOD0, поэтому transfer выбирал первый source renderer (дверь). Group key
  удаляет LOD/collision suffix, сохраняя numeric instance. Настоящие .001 и
  .002 остаются разными группами; имена сцены и FBX не переименовываются.
- 5 pack/pipeline regressions и ещё 5 naming/source-selection cases сначала
  воспроизвели ошибки. Итоговый subset: **273/273 EditMode tests**, без skips,
  Unity 6000.2.6f2 / DX11. Проверены native single/shared packs, input UV0/UV2,
  positive-area overlaps и session release, максимальный int oversample,
  partial/all failed pipeline и фактический source atlas. Обе reference C#
  сборки и identifier check проходят.
- Два полных async запуска с Checker на LOD1/2, manual 2048, per-mesh packing:
  missing meshes = 0. Pipeline rejected shells 85 → 2 после исправления names.
  Узкая pipeline сводка overlaps=5 **не является полным UV scan**.
  Capture подтверждает чистые три Seat LOD1; у LOD2 Seat остаются по 2 degenerate
  faces и 20–22 overlap pairs. Сам source UV2 корпуса уже содержит 32 degenerate
  faces / 13964 overlap pairs. Grouped targets LOD1: 124 / 11830, LOD2: 19 / 4955.
  Это не исправлено сменой transfer method или уменьшением oversample.
- Frozen Capture: 8 пар корпуса/сидений × 6 методов = **48 rows**, по 2
  повторения; deterministic, inputs unchanged, grouped reference matches.
  Ни один метод не даёт чистый корпус. У 5 nearest-method rows overlap scan
  неполный, их counts — lower bounds. [Корпус и данные](MODERN_TRANSFER_REPRO.md#traincarriage-pack-budget-and-instance-source-selection).

## Shelf_C: shared-source overlap и collapse diagnostics — 2026-10-09

- Продолжение PR #225, baseline `f3cbb88`. Исходный `Shelf_C.fbx` и importer meta
  проверены в проекте на E: и скопированы в изолированный Unity 6000.2.6f2 / DX11
  проект. Сварка сохраняет `tangent.w` каждого угла: LOD0 587 → 551 vertices,
  LOD1 449 → 425, изменённых handedness — 0. Присланные TBN warnings на текущем
  импорте не воспроизвелись; новое изменение weld не требуется.
- Два shared-source gate считали пересечение UV0 AABB доказательством overlap.
  Теперь нужны пересечения треугольников положительной площади. Общая граница
  и пустые области bbox не вызывают eviction. Лимит 200000 сравнений на пару
  сохраняет консервативный conflict, но лог сообщает incomplete вместо
  подтверждённого duplicate. На Shelf ложный shared-source conflict исчезает;
  подтверждённый UV0 ambiguity остаётся видимым.
- CollapseDiag сравнивал UV AABB с world AABB: повёрнутая тонкая деталь могла
  выглядеть квадратной в 3D bbox. У target shell #50 реальная anisotropy ≈1.37,
  хотя прежний лог сообщал 12.7:1 vs 1.4:1. Проверка теперь использует метрику
  каждого треугольника и порог anisotropy 5; bbox только описывает дефект.
  UV2 эта диагностика не меняет. Реальные collapsed/stretched charts остаются
  предупреждениями, включая 5.2:1 в отдельном full-pipeline probe.
- 12 подготовленных случаев (forward/reverse, square/1:2 metric,
  off/legacy/adaptive symmetry), 6 методов, 2 повторения: **72 frozen rows**.
  Все UV2 hashes и quality counters совпадают с baseline; outputs deterministic,
  inputs unchanged. Это исправление классификации, не улучшение UV2 Shelf в
  этом corpus. В reverse остаются 22 overlap pairs для square metric и 8 для 1:2.
- **101/101** связанных EditMode tests; обе reference C# сборки и identifier
  check проходят. Два async Full Pipeline с Checker завершились без missing
  render meshes и TBN warnings. Материалы исходной сцены не скопированы;
  реальные failing settings требуют Capture. [Данные и ограничения](MODERN_TRANSFER_REPRO.md#shelf_c-shared-source-and-rotation-invariant-diagnostics).

## Modern furniture: weld и владение мешами — 2026-10-09

- Продолжение PR #225. Копии `Modern_DressingTable_A`, `Modern_Kitchen_A` и
  `speaker-retro` импортированы с исходными importer meta в изолированный Unity
  6000.2.6f2 / DX11 проект. Исходные FBX не менялись; SHA256 сохранены.
- Сварка UV0 теряла противоположный `tangent.w`, предупреждая уже после слияния.
  Теперь такой шов сохраняется. На Kitchen Handle LOD1 в контролируемой adaptive
  подготовке: 24 → 0 degenerate faces, 12 → 0 overlap pairs, area-weighted
  anisotropy 512.56 → 1.05. На других деталях остаются дефекты; это не сертификация
  всей модели. Matching/repack/symmetry core в итоговой правке не изменены.
- Повторный Full Pipeline с Checker воспроизвёл уничтожение привязанных к
  MeshFilter outputs до repaint: Speaker 2, DressingTable 2 и Kitchen 4 missing
  meshes. Операции снимают overrides и ссылки на старые working outputs до
  изменения мешей, затем сохраняют выбранный режим и framing. Проверены также
  Off и Checker с working output в исходном scene binding/preview backup.
- Auto-tune сохранял лучший target transfer, но source atlas оставался от
  последней попытки. Выбранные source/target meshes, atlas dimensions, validation
  и symmetry flags теперь восстанавливаются вместе. Заменённые попытки и snapshots
  освобождаются, включая cancel/exception paths.
- Cross-LOD hints были привязаны только к entry/group, поэтому смена порядка
  островов атласа выбирала соседнюю поверхность. Подсказки теперь принадлежат
  конкретному source mesh; winner restore, refresh и in-place mesh events
  сбрасывают устаревшие source indices/transforms. Все три перехода сначала
  воспроизвели неверный остров в двух-chart fixture и проходят после исправления.
- Новый набор сначала воспроизвёл 3 failures из 9 tests (TBN seam и Renderer
  lifetime), отдельный auto-tune fixture и 2/2 scene-binding fixtures также упали.
  Итоговый affected subset: **353/353 tests**, без skipped/failed. Обе reference
  compile variants с/без FBX exporter проходят. Async full pipeline с обновлением
  canvas на editor updates: два запуска каждой модели, missing meshes = 0.
  Зависание Speaker не воспроизведено; время 0.45–0.68 s на контролируемых настройках.
- Frozen comparison шести методов: 96 rows × 2 repetitions, deterministic,
  inputs unchanged. Повтор после Sonar helper extraction и source-hint fix
  сохранил все quality/reference counters на 96 rows. Эксперимент с mesh-relative
  dedup guard убрал 11 схлопнувшихся
  граней Countertop, но увеличил overlap area до ≈0.001. Geometry fallback не
  решил конфликт. Эксперимент отвергнут, в production его нет; качество не
  объявляется исправленным заменой одного дефекта другим.
- [Корпус, команды, метрики и ограничения](MODERN_TRANSFER_REPRO.md).
  Локальные logs/XML находятся в `_results~/modern-transfer-20261009/`, полные
  frozen reports — в `BenchmarkReports/` изолированного consuming project.

## Синхронизация UV/3D preview после Transfer — 2026-10-09

- Продолжение PR #225, без изменений core matching/repack/symmetry алгоритмов.
  Смена transferredMesh сбрасывала кеши, но не переустанавливала активный Checker.
  Изменение UV на том же Mesh также не наблюдалось hub. Теперь учитываются
  displayed mesh/entry identities и context cache version; переустанавливаются
  Checker, Shells3D и Lightmap, затем собираются актуальные material references
  для того же кадра. Старый transfer output освобождается.
- Lightmap preview клонировало MeshFilter.sharedMesh вместо текущего working mesh;
  GPU layer игнорировал renderer atlas scale/offset и делил одну texture между
  экземплярами одного mesh. Пиксельный тест выявил ещё один дефект: BeginPreview
  переключает сцену, и LightmapSettings внутри preview становится пустым. Атласы
  теперь берутся из snapshot исходной сцены перед BeginPreview, слои принадлежат
  отдельным entries и sampled UV получает renderer scale/offset.
- Preview сохраняется через UV/LOD/context/Undo/reset и save/apply/export,
  включая nested write, early return, cancel и exception paths. Во время записи
  временные overrides сняты. Camera и UV framing не сбрасываются. Surface-area
  display также обновляется при geometry edits на том же Mesh.
- Исходная новая матрица воспроизвела 39 failures из 68 tests на `59d634d`.
  Дополнительно подтверждено замечание PR про radius/clipping после смены модели:
  3/3 новых fixtures упали на `08e13d4`, включая GPU pixels distant content.
  Radius/clip/zoom bounds теперь следуют текущему content независимо от framing;
  pivot/orbit/distance сохраняются. Changelog entries распределены по категориям.
  После исправлений: 104/104 preview tests и 350 passed / 0 failed / 1 skipped
  из 351 affected tests, Unity 6000.2.6f2 / DX11 / Built-in. Пропущенный line-render
  fixture требует URP. Оба FBX compile variants проходят.
  Отдельный изолированный URP 17.2.0 / DX11 проект: 114/114 preview/line tests,
  без skipped, включая тот же integration fixture и GPU pixel assertions.
  Это проверка отображения, не новая оценка correspondence/overlap quality реальной модели.
  [Матрица событий и ограничения](PREVIEW_UPDATE_EVENTS.md).

## Эксперимент 2026-10-08 — Пропорции исходной текстуры и квадратного lightmap

- Исходный случай: текстура 1024×2048 и соответствующие ей UV0. До упаковки UV2
  временная копия получает pixel metric текстуры с учётом tiling материала:
  U × sqrt(width/height), V × sqrt(height/width). ARAP и density работают уже
  в этой метрике; исходный UV0 с исходной размерностью, форматом, stream и raw bytes
  восстанавливается в finally.
  Разные или неизвестные аспекты используемых материалов одного меша не исправляются
  одной общей матрицей. Пустые submeshes не участвуют в выборе метрики.
- Дополнительно воспроизведено влияние активного Checker/Shells3D/Lightmap preview:
  resolution метрики читало подменённые renderer materials, поэтому исходный
  прямоугольный texture aspect становился квадратным. Read-only lookup теперь
  берёт сохранённые исходные material slots и tiling. Он не снимает preview,
  не меняет выбранный режим, renderer references или UV0. Три regression fixtures
  сначала воспроизвели неверную метрику во всех режимах, затем прошли с исправлением.
- Дополнительный дефект подтверждён native-тестом: bridge нормализует координаты
  отдельно по ширине/высоте прямоугольного packed atlas. В UV transfer workflow
  результат переводится в квадратную метрику max(width,height) на стороне C#,
  включая размеры, переданные transfer. Border inset применяется после перевода
  в квадрат, чтобы сохранить указанный отступ в пикселях на обеих осях.
  Native source и binaries не менялись.
- Opt-in Capture сохраняет точные меши/параметры/меж-LOD hints и этапы симметрии,
  repack и transfer. Trace записывает реально оценённые кандидаты без дополнительных
  matching queries; Replay сравнивает baseline и два запуска. Schema 2 выносит большие
  stage/pair данные в checksum-protected details JSON; manifest имеет общий лимит
  writer/reader 64 MiB, payloads — 512 MiB. Replay также выносит массивы каждой пары
  в свой details JSON и держит только summaries в replay.json. Schema 1 по-прежнему читается.
  Добавлены axis stretch
  против 3D и positive-area overlap, включая same-shell пары. Алгоритмы symmetry,
  overlap repair и критерий выбора auto-tune не менялись.
- Проверка после исправления замечаний PR #225: 340 passed / 0 failed / 1 skipped,
  Unity 6000.2.6f2 / DX11. Пропущен fixture совместных Standard/URP normal conventions:
  в изолированном test project отсутствует URP source shader.
  При выключенных ARAP/density source aspect ON даёт anisotropy ≈1, OFF ≈2 после
  repack и transfer; UV0 (включая третий компонент) сохранён. Capture/Replay двух
  LOD с hints совпадает по UV2 hash и mapping; trace on/off не меняет результат.
  Подтверждены прежние shell IDs, scalar UV0 и auxiliary UVs в readable/unreadable
  working copies и snapshots, отказ общей коррекции при textureless used material,
  pixel border после repack, capture payloads суммарно >64 MiB с компактным manifest,
  legacy replay, проверка checksum и обработка ошибок capture/IMGUI.
  После проверки и интеграции Sonar autofix отдельно повторён затронутый набор:
  42/42 tests (transfer capture, shell extraction, geometry helpers, mesh access),
  без skipped. Это subset полного прогона 341 tests, а не другой полный прогон.
  Обе FBX compile variants прошли после autofix.
  Последующие preview/UV0-format/replay-report правки проверены 138/138 tests без
  skipped (transfer capture, mesh access, FBX export, preview LOD switches, viewport),
  Unity 6000.2.6f2 / DX11. Float16 UV0 сохраняет формат, stream и raw bits после
  working copy и native repack/transfer; replay payloads суммарно >64 MiB не растят
  общий summary report.
  Реальная модель пользователя пока не захвачена новым кодом, её overlap/symmetry
  проблема не объявляется исправленной. [Использование и ограничения](TRANSFER_CAPTURE.md).

### Качество прямого и обратного переноса — 2026-10-08/09

- Продолжение того же texture-metric эксперимента в PR #225. Бенч получил
  `includeHigherDetailTargets`: source LOD1 можно переносить на LOD0. Подготовлены
  81 пары: аналитические контроли, шкаф в обоих направлениях, symmetry
  off/legacy/adaptive, ARAP ON/OFF и стул. Шесть методов запускаются на одном
  portable corpus, с warmup и тремя измеряемыми повторами; source UV2 между
  методами не перепаковывается. Подготовка использует копии импортированных FBX,
  отдельный атлас пары и заданный аспект 4096×8192, а не UI Full Pipeline.
- Найдены независимые дефекты transfer: маленький UV-треугольник при фиксированном
  пороге Gram determinant превращался в первую вершину; поиск source прекращался
  на первом «достаточно близком» chart; мягкий штраф нормали выбирал обратную сторону
  тонкой поверхности, когда LOD-вершина переходила ближе к ней. Теперь UV barycentric
  использует double cross product и ближайший сегмент для настоящей вырожденности;
  3D queries используют общий TriangleBvh helper. Проверяется весь ограниченный
  набор source-кандидатов. Совместимая нормаль переопределяет только победителя,
  обращённого в противоположную сторону. Review regression на U-образном chart
  подтвердил, что без этого ограничения средняя нормаль целого chart отвергала
  точный фрагмент боковой стенки в пользу удалённой плоскости. Ортогональная средняя
  нормаль теперь сохраняет преимущество точного surface match. Сохранённая
  source-вершина с теми же 3D/UV0 получает
  точный UV2; неоднозначные совпадения с разными UV2 исключаются из этого пути.
- Laplacian post-pass больше не перемещает корректную тонкую грань на противоположное
  ребро. Проверяется уже ограниченное перемещение: сохранение winding, не менее 10%
  прежней площади и отсутствие нового axis stretch. Почти нулевая ширина от float
  roundoff теперь также считается дефектом кандидата (anisotropy >10,000), с учётом
  3D-геометрии, чтобы не отвергать изометрические slivers.
- Symmetry и SpatialPartitioner требуют positive-area UV intersection. Общая ячейка
  сетки, ребро или вершина не являются доказательством overlap; реально сложенные
  соседние грани выявляются. Ограниченные сканы не объявляют отсутствие witness
  доказательством чистой развёртки. Для чистого, единолично используемого source chart
  доступны проверенные fallback для смещённой UV0-строки и геометрической проекции.
  Они должны уменьшать число дефектов и пройти внутренний overlap scan; shared
  fragments и stacked source не переводятся в этот путь.
- В source preparation обнаружено обратное схлопывание ARAP: all-zero UV0 разворачивался
  через Tutte/ARAP, затем uniform scale возвращал нулевую площадь. Теперь сохраняется
  оставшаяся длина или масштаб геометрического решения. Полностью коллинеарный input
  также запускает Tutte, даже если диагональная линия имеет ненулевой bbox. Отдельная
  растянутая грань получает точную развёртку в собственной tangent plane без ARAP solve.
  На `Grandma_Cabinet_B_Dpanel_B` исчезли все 14 вырожденных source/target UV2-граней
  при ARAP ON. Исходный UV0 остаётся нетронутым; ARAP OFF сохраняет прежний контракт.
- Проверено в Unity 6000.2.6f2 / DX11: 189/189 EditMode tests, без skipped, включая
  native xatlas, shared density, capture/replay, forward/reverse analytic atlas,
  thin-sheet normal disambiguation, curved-source fragments, tiny UV charts,
  source collapse, preview material metrics и IMGUI regressions.
  Обе FBX compile variants проходят. Отдельно сохранён replay старых 41 frozen inputs
  для проверки transfer без новой подготовки source. Artifact reports имеют dirty-state
  provenance; записанный результат capture не является независимой correspondence truth.
- Ограничения остаются измеримыми: shared-source fragments могут давать cross-shell
  наложения; reverse transfer на более подробную геометрию не восстанавливает отсутствующую
  поверхность и UV2 seams. Shape/overlap metrics не доказывают правильную correspondence.
  Прямой и обратный перенос не объявлены полностью исправленными. Альтернативные
  global UV0/3D nearest методы дают значительно больше наложений на этом cabinet corpus;
  их меньший vertex miss count не означает лучшую lightmap-развёртку.

### Pre-optimize и иглы на целевом LOD — 2026-10-09

- Продолжение эксперимента PR #225. Пользователь подтвердил ухудшение после Transfer
  на целевом LOD при включённом `Pre-optimize (meshopt dedup)`. Старый этап сравнивал
  только position/normal/UV0 и склеивал вершины независимых chart, касающихся одной
  точкой. Extractor объединяет грани по общему индексу вершины; такой merge меняет
  connectivity и убирает возможность хранить разные UV2 на совпадающих углах.
  Полная byte equivalence также не доказывает, что два chart следует соединить.
- Два аналитических regression tests сначала упали на реальном native пути:
  8 вершин превращались в 7, два UV0 chart — в один; последующий Transfer давал
  2 вырожденные грани. Теперь UV pre-optimization выполняет cache/overdraw triangle
  ordering через прежний native ABI с уникальной identity каждой вершины и обратным
  декодированием fetch remap. Вершины, raw streams, formats, skinning, blend shapes
  и material boundaries сохраняются. Никакого geometric weld flag этот этап не
  устанавливает; решение о слиянии остаётся за UV-aware edge weld.
- Imported-FBX preparation получил независимые opt-in `weldUv0`/`preOptimize`.
  На 21 части шкафа LOD0 → LOD1 сравниваются два одинаковых набора с edge weld,
  adaptive symmetry, ARAP/density и aspect 4096×8192; меняется только pre-optimize.
  По 2 измеряемых повтора, 42 строки на прогон, без warmup. Старый pre-optimize дал
  405 positive-area source overlap pairs и 263 target pairs против 0/0 в контроле;
  target vertices уменьшились с 3419 до 3183. На Dpanel_A area-weighted anisotropy
  выросла с 1.130 до 8.423. После исправления оба варианта дают 3419 target vertices,
  0 source/target overlaps и одинаковые целочисленные quality counters; anisotropy
  отличается только малым численным округлением после изменения порядка граней.
- Unity 6000.2.6f2 / DX11: 193/193 affected EditMode tests, без failed/skipped,
  включая actual workflow point-contact fixtures и сохранение Float16 UV0 2D/4D,
  color raw bits, skinning, blend shapes и пустого material slot. Оба C# compile
  variants проходят. Все 42 строки до/после детерминированы, сохраняют frozen inputs
  и имеют complete overlap scans. Local artifacts: `_results~/pre-optimize-20261009/`;
  provenance — `075fa92` плюс recorded dirty working tree с этим patch.
- Замечание Sonar S6640 устранено managed-упаковкой position/identity через
  переиспользуемые массивы и `Buffer.BlockCopy`, без unsafe блока. Проверен diff
  автоматического commit `2cb376e`: 64/64 tests capture/quality и повтор всех 42
  imported-FBX строк на clean checkout сохраняют те же quality counters. Повтор
  имеет `gitDirty=false` и SHA `2cb376e`; это отдельная проверка после 193-test subset.
- Контроль и исправленный pre-optimize сохраняют 1 вырожденную target-грань и 1299
  stretched faces на шкафе: устранено именно добавочное ухудшение от dedup. Это
  отдельные атласы пар, не UI auto-tune/shared-atlas run; reverse correspondence,
  исходное локальное растяжение и качество произвольных моделей не объявляются
  исправленными. Native source и binaries не менялись.

### Общий бюджет плотности в совместном атласе — 2026-10-08

- Лог пользователя v1.1.34 и CSV `20261008_202301_950` подтверждают проблему при
  `repackPerMesh=0`: `RepackMulti` давал каждому из 21 меша отдельный бюджет
  `targetUvCoverage`, затем собирал все charts в один атлас. Мелкие детали получали
  такую же суммарную UV-площадь, как корпус. Поле старого CSV `texelDensityMedian`
  имеет единицы 3D-area / UV-area, а не tex/m; его разброс 2.514–337.634 означает
  приблизительно 11.59× по линейной плотности.
- Pre-pack normalization теперь выполняется после ARAP всех мешей с общей целью:
  `coverage / sum(surfaceArea)`. При отключённом coverage budget используется
  общая подготовленная UV-площадь; сохраняется единственный общий масштаб плотности.
  `RepackSingle` и отключённая нормализация сохраняют прежний контракт.
- Post-pack correction совместного атласа тоже получает общую цель. Медиана
  взвешена 3D-площадью: большое число маленьких charts, раздутых округлением xatlas,
  не должно определять плотность корпуса. Коррекция остаётся shrink-only вокруг
  центроида каждого packed chart; острова не перемещаются между слотами.
- `JointAtlasDensityTests` проверяет бюджет и одинаковую плотность поверхностей
  площадью 1 и 16, сохранение UV0, native output при normalization ON/OFF.
  Изолированный FBX-бенч работает с копиями `Grandma_Cabinet_B_Destructed.fbx`
  в проекте из `Tools~/prepare-transfer-benchmark.ps1`, пишет per-triangle medians
  UV-area / 3D-area и отдельные CSV для authored/ARAP и post-correction вариантов.
  Контроль воспроизводит прежний per-mesh бюджет с ARAP OFF; ARAP-вариант является
  дополнительной проверкой подготовки, а не сравнением одной изменённой операции.
- Тесты плотности не объявляют исправленными intra-chart stretch, симметрию,
  overlaps или LOD matching. Ни исходный FBX, ни проект пользователя на E: не меняются.
- Unity 6000.2.6f2 / DX11: 69/69 tests passed, 0 skipped; обе FBX compile variants
  прошли. На 21 исходном меше контроль с прежним бюджетом имеет разброс линейной
  плотности 12.145×. Общий бюджет даёт 1.367× без post-correction, 1.151× с общей
  post-correction (ARAP OFF); дополнительный ARAP ON прогон даёт 1.153×.
  Это медианы треугольников каждого меша, а не гарантия одинакового texel footprint
  каждого треугольника. Подтверждены сохранение UV0 и прежний одиночный режим.

### Автономное сравнение transfer — 2026-10-08

- Добавлен [бенч зафиксированных входов](TRANSFER_COMPARISON.md): шесть методов,
  семь аналитических случаев, native source preparation на копиях импортированных
  FBX с aspect ON/OFF и symmetry off/legacy/adaptive. Production transfer и выбор
  auto-tune не менялись. Подготовленные входы сохраняются как переносимый capture.
- Изолированный Unity 6000.2.6f2 / DX11 прогон исходных FBX с E: стул и 21 группа
  `Grandma_Cabinet_B_Destructed`, плюс матрица подготовки трёх групп шкафа.
  Всего 41 случай × 6 методов = 246 строк, warmup 1, measured repeats 3 и отдельный
  trace run; UV2/mapping повторяемы, input mesh hashes сохранены.
- В базовой adaptive-подготовке 21 группы текущий grouped transfer дал 75
  positive-area triangle overlap pairs, 74 degenerate faces и 1441 stretched faces.
  Это реальные дефекты качества, а не ошибки выполнения бенча. Source atlas
  `Dpanel_A` уже имеет большую анизотропию (area-weighted ≈542.6) до transfer;
  у `Handles` transfer даёт большую анизотропию (≈58770.2) при source ≈1.9.
  Эти случаи пригодны для раздельной проверки source repack и matching/topology.
- При ARAP/density OFF на трёх выбранных группах source anisotropy с aspect
  OFF в среднем по группам ≈2.08, с aspect ON ≈1.09. Подготовка использует явный
  аспект 4096×8192 из исходного Wood Trim albedo (также 1:2); размеры импортированной
  текстуры могут быть уменьшены без смены аспекта.
- Прогон импортированных sub-assets не воспроизводит UI auto-tune/weld, material
  tiling/conflicts или индивидуальные renderer transforms. Для точного выбранного
  запуска всё ещё нужен next-run capture. Внутри бенча reference для реальных FBX
  является recorded production baseline, не independent ground truth; дефекты
  overlap/symmetry не объявляются исправленными.

## Эксперимент 2026-10-03 — Chart-merge постпроцессинг Unwrap (UV0)

- **UV repair contention 2026-10-05 (продолжение Exp #1):** занятая legacy
  xatlas session ошибочно превращалась в отказ обязательного overlap repair.
  Repair теперь ждёт session на worker с отменой, обычный optional repack
  сохраняет fail-fast. 95 Unity tests passed, включая ожидание, отмену без
  освобождения чужого atlas и сохранение corners; обе FBX compile variants passed.
  Свежий capture 1598 faces / 1024 px воспроизвёл 72 charts / 5 overlap pairs;
  с heuristic packing шесть off/on повторов дали 74 / 47 charts, zero overlaps,
  mean/worst 1.02925/3.07848 и 1.03342/1.68071, одинаковые buffers и corners.
  Original Best packing не завершил первый pack за 6 min 35 s и был остановлен
  только в изолированном harness; его full replay не подтверждён. Production
  packing settings, native source/binaries и quality gates не менялись.

- **Первый trainer 2026-10-04 (подготовка следующего этапа Exp #1):**
  `train.py` перепроверяет independent train/val/test и hashes/geometry, использует
  train-only normalization и val-only best/threshold; модель/optimizer/RNG
  сохраняются для epoch resume. Алиасы одной geometry балансируются как один
  model_group. На synthetic tetrahedra CUDA whole 4 epochs и pause2/resume4
  дают одинаковые model/optimizer/metrics; recovery final JSON не повторяет test.
  Текущий один бюст отклонён до output creation. LearnedSeamBenchmark:
  29 passed / 1 Windows symlink fixture skipped; SeamPlacementBenchmark: 18 passed.
  Это проверка trainer, не обученное художественное UV-решение. Пользовательский
  набор и RTX 3070 Ti ещё не участвовали в quality run. [Проверка](LearnedSeamResearch/TRAINER_VALIDATION.json),
  [запуск на ноутбуке](../Tools~/LearnedSeamBenchmark/README.md#first-local-training-cycle).

- **Геометрическая фаза завершена 2026-10-04 (продолжение Exp #1):**
  PartUV/OptCuts/AutoUV и merge не воспроизвели требуемое расположение швов.
  Проверены SHA/correspondence/winding 19 captures из 22 опубликованных rows;
  18 полных overlap scans и 26 seam V1 rows заново совпали с сохранёнными числами.
  SeamPlacementBenchmark: 18 passed; LearnedSeamBenchmark до trainer:
  21 passed / 1 Windows symlink fixture skipped. Чужие solvers и Unity tests
  заново не запускались. Следующий этап — обучаемые швы с независимыми families;
  два текущих UV-варианта бюста остаются одним asset. [Итог и рендеры](UV_GEOMETRY_EXPERIMENT_FINAL.md).

- **Протокол набора 2026-10-04 (продолжение Exp #1):** основа — существующие
  high-poly → построенные с нуля авторские low-poly, история ручных исправлений
  не обязательна. Оригиналы/native scenes/карты/зависимости сохраняются отдельно
  от производных features; новый `archive.py` копирует явный manifest в свежий
  каталог с input/output SHA256, без parsing или подтверждения полноты сцены.
  Для будущего topology обучения реальные remesh/simplify outputs — основной
  поток, несколько способов порчи — дополнительный. Canonical geometry перед
  порчей, family split до генерации и test на реальных ошибках предотвращают
  часть утечек; обученный результат пока не получен. Уточнено, что seam preparer
  принимает manifold open meshes и сам не распознаёт случайные дырки.
  Текущий LearnedSeamBenchmark: 21 passed / 1 symlink fixture skipped;
  реальная Windows junction-проверка прошла, пользовательские модели не копировались.
  [Полный протокол и ограничения](UV_LEARNED_SEAMS.md).

- **Обучаемые швы 2026-10-04 (продолжение Exp #1):** новый AutoUV baseline
  при cone 35/50/65 сохраняет геометрию, но даёт 51/59/42 intra-chart overlap
  pairs и seam F1 0.213/0.172/0.240. Контроль с точными авторскими cuts, без
  авторских UV coordinates: uniform disk seed → SLIM → square-normalized xatlas,
  zero overlaps, mean/worst 1.105/4.523, fill 50.45% против ручных 69.14%.
  Standalone packing дополнен uniform square normalization, production unchanged.
  Подготовщик labels предотвращает leakage по asset/geometry/family: два UV
  варианта бюста — одна модель. Untrained graph probe 79169 parameters прошёл
  FP32 forward/backward на GTX 980 Ti, peak PyTorch allocated 53.7 MiB;
  обучение/качество модели не проверены, набор пользователю ещё нужно собрать.
  18 seam/control + 10 prepare/graph checks passed, все UV renders сохранены.
  [Методы, доступность релизов, фактические замеры и ограничения](UV_LEARNED_SEAMS.md).

- **Лог из E project 2026-10-04 (продолжение Exp #1):** native source fitting
  и area cleanup воспроизводят одиночное отверстие на том же участке бюста;
  компактный fixture из 8 vertices воспроизводит его при voxel 48. Raw solid
  теперь проверяется до Trim с проверенным retry без solve. Полностью отклонённая
  source-коррекция пробует три меньших шага при прежних gates, лог сохраняет
  attempted movement и RMS/max причины отказа. Cage fit и preview используют
  ту же source-facing/two-sided политику, что bake. Точный UV input из E повторён
  off/on трижды: 84 → 32 charts, zero overlaps, неизменные triangle corners.
  Новый closed 1598-face replay проходит четыре Unwrap после исправления
  budget-aware progressive repair precision; 241 Unity tests passed/skipped0.
  [Причины, ограничения, реальные рендеры и replay](REMESH_LOG_20261004.md).

- **Voxel stair triangles 2026-10-04 (продолжение Exp #1):** source-guided
  fitting, tangent redistribution и локальные edge flips подключены к Simplify
  через Fit to source surface. Source-distance, boundary/Euler и collapse-budget
  gates отклоняют ухудшающие кандидаты; финальная коррекция сохраняет 1886 / 98
  faces и уменьшает slivers 42 → 34 на бюсте, 30 → 21 на диагональном боксе.
  Четыре конечных Unwrap имеют zero overlaps и сохранённые source corners;
  маленькие charts при repair могут повторно паковаться с большей точностью,
  без ослабления stretch gate. Native source/binaries unchanged.
  [Методика, ограничения, реальные сетки и UV](VOXEL_SURFACE_REFINE.md).

- **Крупный piecewise OptCuts shell (продолжение Exp #1):** semantic hierarchy
  делится по surface-area cap и пересекается с mandatory planar regions. При
  cap 20% / symmetric Dirichlet 4.15 крупнейший chart сокращён 63.54% → 16.55%,
  mean/worst 1.260/7.401 → 1.076/3.716, UV-induced sliver faces 20 → 5,
  zero overlaps. Сетка/грани не изменены; 37 intrinsically elongated faces
  остаются. Более строгий Ed 4.05 при тех же partitions даёт 2 UV-induced faces,
  но ухудшает mean stretch до 1.158 и fill до 49.05% (Ed 4.15: 51.13%).
  Сохранены рендеры cap 15/20/30%, Ed 4.05 и сравнение с первым piecewise;
  seam parity всё ещё неполная, в Unity defaults метод не включён.

- **Расположение швов 2026-10-04 (продолжение Exp #1):** сравнение по длине
  швов на одинаковой 1856-face геометрии подтверждает ухудшение recall после
  merge: V1 50.12% → 30.17%, V2 52.45% → 29.80%. Проверены полный PartUV
  native pipeline (ABF, PAMO off, thresholds 1.1/1.25/1.5), OptCuts со свободными
  и обязательными региональными границами, а также geometry-only segmentation.
  Для каждого полного unwrap сохранён PNG и positive-area overlap scan;
  11 новых Python checks проходят, standalone xatlas probe собран. Новые Unity
  defaults не приняты: лучшие по числу charts варианты не восстанавливают
  пользовательские границы. [Методика, рендеры и числа](UV_SEAM_PLACEMENT.md).

- **Свежий 5125-face input 2026-10-04 (продолжение Exp #1):** точный capture
  воспроизводит 147 baseline charts → 44 full merge → packing rejection → 96
  после прежнего halving. Поиск до шести budgets сохраняет прошедшие checkpoints:
  итог 51 charts / 16 small / 52.366% fill / zero overlaps, против 96 / 37 /
  52.726%. Существующие UV cuts вне предлагаемого edge seam теперь допускаются.
  Все final gates сохранены; 194 Unity tests passed, три off/on repeat runs
  побайтово идентичны с сохранением source corners. Обе FBX references остаются
  на 22 charts / 54.633%; до ручного качества всё ещё есть разрыв. Цена на новом
  input: ~13.8 s вместо ~5.9 s; дорогое brute-force / >512 px packing сохраняет
  прежний halving / first-success путь. Relax during growth, compactness, rotations и
  explicit-density packing проверены отдельно; не приняты как defaults.
  [Причина, сравнение layouts, отвергнутые варианты и replay](UV_MERGE_SEARCH.md).

- **Входная topology 2026-10-04 (продолжение Exp #1):** на бюсте source/raw
  voxel замкнуты; Trim удалял 4 faces и создавал 12 открытых edges, Simplify
  создавал 3 opposite-winding duplicate pairs. Trim теперь сохраняет closed
  source volume components; Simplify удаляет только безопасные collapsed fins,
  фиксирует реальные borders и проверяет edges, vertex fans и component Euler
  characteristics с retry/fallback. Результат None: 1886 faces, boundary /
  non-manifold / duplicates / degenerate = 0; 9 repeat runs в трёх режимах
  бит-идентичны. 192 Unity tests passed, skipped 0; native ABI/binaries unchanged.
  [Причина, результаты и воспроизведение](SIMPLIFY_TOPOLOGY.md).

- **Source signals 2026-10-04 (продолжение Exp #1):** curvature/cavity на high-poly
  и geometric AO перенесены через production fitted cage на оба ручных эталона
  и actual simplify. 16704 + 17940 samples, zero misses; повторные buffers
  бит-идентичны. На пяти spatial folds source повышает hard-boundary AUC
  0.844→0.903, но общий UV AUC только 0.808→0.827 / 0.801→0.830.
  После исключения hard edges преимущество для UV-разрезов исчезает:
  V1 0.530→0.505, V2 0.703→0.690. Blind source-driven default weights
  не вводились; нужны отдельно confidence и chart topology/cut planning.
  Метод, AO/curvature radii, контроль leakage и sampling limits:
  [отчёт](UV_SOURCE_SIGNALS.md), переносимый harness в `Tools~/SourceSignalsBenchmark/`.

- **Сравнение с двумя ручными эталонами 2026-10-04 (продолжение Exp #1):**
  `uvbest.fbx` содержит исправленную сетку: 1856 triangles, без duplicate faces и
  non-manifold edges. На этой же сетке прежний merge оставлял 52 islands против
  ручных 18/23. Ограничение не сводится к топологии или настройкам xatlas:
  12 проверенных charting-профилей без merge дают 72–80 islands.
  Теперь проверяются два независимых кандидата: прежняя подгонка шва (residual 2%,
  local worst 4) и более свободная (10%, local worst 6), затем прежние relax,
  полный overlap scan, repack и неизменные global gates. Свободный кандидат
  выбирается только при меньшей фрагментации и сохранении упаковки относительно
  уже проверенного строгого кандидата. На эталоне: 52 → 22 islands, fill 54.6%,
  zero overlaps. На старом 1997-face входе потеря fill 64.6% → 58.6% отвергается.
  Отмена на любом этапе сохраняет исходный объект. Во втором ручном эталоне
  обнаружено 16 positive-area intra-chart overlaps в двух charts; первый чистый.
  Числа, layouts, smoothing groups и ограничения: [отчёт](UVBEST_REFERENCE_ANALYSIS.md).

- **Packing gate 2026-10-04 после плохих пользовательских скриншотов (продолжение Exp #1):**
  zero overlap / низкий conformal stretch не гарантируют плотный атлас или равную
  texel density. UvMesh repack теперь получает charts с UV-площадью, нормализованной
  к 3D-площади; также для overlap repair без Merge. Seams приоритетны по доле
  удаляемой границы, а не chart id. Финальный gate ограничивает потерю заполнения
  5% относительно repaired baseline и chart density CV; при отказе — restart из
  snapshot с половиной merge budget. Все старые overlap/stretch/source-corner
  гарантии сохранены. На 1901 faces: старый merge 42.1% → новый 56.8%, 55 charts;
  на 1997 faces: 47.2% → 64.6%, 56 charts. Это не точный 1883-face snapshot скриншота:
  UV-only captures не содержат исходной геометрии. Теперь Info/RemeshDiag сохраняет
  полный input + настройки в `unwrap_*.bin`, последние пять файлов.
  Пределы, сравнение baseline, ограничения и реальные layouts: [отчёт](UV_PACKING_QUALITY.md).

- **Distortion relax 2026-10-04 (продолжение Exp #1):** после успешного merge
  все итоговые charts проходят guarded conformal relax перед repack.
  Свободная граница, MIPS-based energy + quadratic tail, L-BFGS (до 50 итераций),
  аналитический первый корень det-barrier и сохранение безопасных checkpoints.
  Per-chart mean/worst не увеличиваются; UV-площадь сохраняется uniform scale,
  seam/tangent дубликаты получают бит-идентичные UV; полный overlap scan обязателен.
  Source corners не меняются; ambiguous UV cuts / unsupported topology пропускаются.
  ARAP и разные места relax сравнены: all-after даёт устойчивый выигрыш без
  изменения числа shells; before меняет seam candidates и не всегда полезен.
  На bust recommended 512: 28 charts / 14 small / 0 overlap,
  mean 1.10772 → 1.05221, worst 3.75916 → 1.66454; user settings:
  36 / 19 / 0, mean 1.18992 → 1.06325, worst 3.19396 → 1.65894.
  По 10/10 production repeats побайтово идентичны; каждый source corner сохранён.
  На default 2048: 29 / 13 / 0, worst 3.90045 → 1.67871 (3 repeats).
  Relax занимает около 40 ms; pack на 2048 всё ещё дорогой (~11 s).
  Отмена после принятого merge теперь восстанавливает полный snapshot;
  диагностический лог `[UV] merge-relax` отделяет этот этап от финального pack gate.
  165/165 связанных Unity EditMode tests проходят (DirectX, без skips);
  compile-check проходит в обеих FBX define-конфигурациях.
  Метод, alternatives, corpus CSV и UV heatmap: [отчёт](UV_MERGE_RELAX_BENCHMARK.md).
- **Подбор xatlas defaults 2026-10-04 (продолжение Exp #1):** на фиксированном
  bust (1997 faces) и четырёх геометрических fixtures сравнены chart weights,
  iterations и packing: 810 phase/case/profile измерений, 1568 вызовов.
  Сбалансированный default меняет только roundness 0.01 → 0.5; cost 2,
  normal deviation 2, straightness 6, seam 4, iterations 1, rotation on,
  block alignment / brute force off остаются прежними. На bust 512/padding 2
  с Merge: 28 charts / 14 small / 0 overlap, mean 1.10772, worst 3.75916,
  451 ms; текущие сохранённые настройки пользователя: 36 / 19 / 0,
  mean 1.18992, worst 3.19396, 2615 ms (один медленный sample).
  Worst ухудшается, но остаётся ниже 4; это компромисс, не универсальный optimum.
  На 2048 без Merge новый профиль избегает дорогого overlap repair:
  890 ms против 10577 ms у прежнего default. Merge на 2048 всё ещё дорогой
  и остаётся opt-in. Сохранённые настройки не мигрируют автоматически;
  кнопка **Recommended xatlas settings** применяет только chart/packing preset.
  154/154 связанных Unity-теста проходят. Полный корпус, ограничения,
  sample counts и воспроизводимый harness: [отчёт](XATLAS_DEFAULTS_BENCHMARK.md).
- **Изменение алгоритма 2026-10-04 после реальных логов:** baseline пользователя
  уже содержал 26 внутри-chart overlap-пар на 1883 faces / 80 charts; rollback
  возвращал их без изменений. Кандидат 54 charts содержал 33 пары и 6 charts
  с разной намоткой граней. Отказ вызывала mixed winding, хотя mean/worst
  укладывались в пределы. Соседи по welded edge ошибочно исключались из overlap-теста.
  Теперь перед merge (и при выключенном merge) конфликтующие UV-грани разрезаются
  в отдельные charts: детерминированное greedy vertex cover графа конфликтов,
  дублирование вершин на границах, сохранение каждой 3D-грани и формы её UV-треугольника,
  затем repack. Результат принимается только после полной проверки нулевого overlap,
  winding и прежних stretch-пределов; незавершённая проверка не считается успехом.
  Merge сначала выравнивает намотку двух charts отражением UV при необходимости,
  затем делает прежний similarity fit. Проверка площади пересечений общая с
  диагностикой, без исключений для соседних граней. Снап охватывает все tangent/normal
  дубликаты швовой вершины; неоднозначные существующие UV-разрезы внутри chart не сливаются.
  Readback читает полные native vertex/index buffers и проверяет xref каждого
  source corner; chart splits допустимы, геометрия сохраняется. Тангенты пересчитываются
  у всех упакованных charts. Прямоугольный native atlas встраивается в квадратный UV
  с единым масштабом: независимая width/height-нормализация bridge создавала stretch
  1.43 даже на двух исходно недеформированных треугольниках.
- **Проверка на модели пользователя в Unity 6000.2.6f2:** исходный FBX
  `Meshy_AI_Distinguished_Bust_0925154648_texture`, текущие сохранённые настройки,
  отдельный локальный проект. Remesh совпадает с логом: 30288 → 51612 triangles,
  trim 4. Текущая simplification даёт 1997 faces (в прежнем логе — 1883, это другой
  snapshot настроек). Raw unwrap: 78 charts / 8 overlap-пар; repair: 82 charts /
  0 overlap; merge: 36 charts / 0 overlap (46 accepted merges). Small charts: 47 → 19;
  mean stretch: 1.17660 → 1.18992; worst: 2.90240 → 3.19396, пределы 1.29426 / 4.
  Все overlap-проверки полные, invalid/degenerate/OOB = 0; source corners сохраняются.
  10/10 unwrap-прогонов дают побайтово идентичные position/UV/index/chart buffers.
  153/153 связанных Unity EditMode-теста проходят с DirectX (UV диагностика,
  merge/repair, Remesh bake/hierarchy/normals); обе FBX define-конфигурации compile-check
  проходят. Для GPU-тестов нужен графический backend, `-nographics` даёт Null GPU.
  Это проверка Unwrap, не визуальный bake seam-review и не Playground GO-протокол.
- **Диагностика 2026-10-04:** логи `[RemeshDiag] [UV]` при уровне Info разделяют
  `merge-baseline`, `merge-candidate`, `merge-before-pack` (только внутри chart),
  `merge-after-pack (candidate)`, `merge-quality-gate`, `merge-rollback` и
  `unwrap-final mergeCharts=True/False`. Число acceptedMerges относится к кандидату,
  не к финальному результату. Gate печатает baseline/candidate, реальные mean/worst
  limits, valid и все причины отказа. Финальный атлас проверяется и с выключенным merge.
  Overlap-диагностика пересекает UV-треугольники по площади в double precision,
  не пропуская соседей с общим ребром: контакт нулевой площади разрешён, складка — нет.
  Печатаются pair counts внутри/между chart, первые 8 пар с face/chart ID (с нуля),
  сумма площадей пересечений пар (не union; тройное покрытие считается несколько раз),
  degenerate/invalid/OOB и время. Лимит 2M broad-phase сравнений ограничивает стоимость;
  `complete=False` означает только нижнюю оценку, а не доказательство отсутствия overlap.
  Отсекается площадь ≤ max(1e-16 UV², 1e-8 площади меньшего треугольника).
  Само логирование read-only; актуальные изменения алгоритма и приёмки описаны выше.
- **Replay `repack_20261004_031155.bin`:** 1883 faces, 48 charts в merge-кандидате;
  до pack 40 внутри-chart overlap-пар (pairAreaSum=0.000512322007 в UV² дампа),
  после pack 40 внутри-chart пар, 0 между-chart пар
  (pairAreaSum=0.000752826608 в UV² финального атласа). Обе проверки полные.
  Исходный атлас до merge и атлас после rollback этот дамп не содержит, поэтому
  наличие overlap в финальных 80 shell нужно проверять `merge-baseline`/`unwrap-final`.
  10 чистых диагностических тестов прошли в headless harness с math shims;
  Editor/Tests компилируются в обеих FBX define-конфигурациях. Это не Unity EditMode-прогон.

- **Вопрос:** можно ли существенно сократить xatlas-фрагментацию (число островов и мелких
  обломков) вообще без source-features и без нового parameterizer — только пост-обработкой
  выходных чартов?
- **Изменение:** переключатель **Merge charts** (off по умолчанию) в Remesh & Bake → Unwrap.
  После выбора лучшей развёртки (включая «Reduce UV fragmentation») `UvChartMerge.Apply`
  детерминированно, раундами, пробует слить соседние пары чартов: Procrustes-подобие
  (поворот + uniform scale + перенос после выравнивания winding) UV движущегося чарта по швовым вершинам,
  затем бит-точный снап шва к UV принимающего — xatlas соединяет чарты по UV-colocal,
  `faceMaterial` только разделяет, поэтому почти равный шов после repack снова распадётся на
  два острова. Гейты приёмки: residual шва ≤ 0.02 относительно диагонали UV bbox принимающего;
  scale фита в [0.5, 2] (texel density); `maxChartArea`/`maxChartBoundary` пользователя
  (boundary объединения = boundaryA + boundaryB − 2·seamLength3D); полный tri-tri overlap-тест
  (double-precision polygon clipping, разрешён только контакт нулевой площади); локальное
  растяжение после снапа на гранях движущегося чарта и швовых гранях принимающего:
  mean ≤ max(1.15, pre·1.1), worst ≤ max(4, pre·1.1), единая winding. Тангенты всех чартов
  перестраиваются по финальной раскладке атласа (Lengyel-аккумуляция по граням) — это
  покрывает и поворот merge-фита, и по-вершинное смещение снапа шва, и per-axis
  ceil-растяжение паковщика xatlas (не являющееся подобием). После снапа дополнительно
  проверяется само-перекрытие движущегося чарта (снап — не подобие и может сложить плотно
  свёрнутый чарт сам на себя). Слитые чарты перепаковываются через xatlas UvMesh bridge (`faceMaterial` = id
  чарта, ComputeCharts + PackCharts; retry с обоими rotation-флагами off при неоднозначном
  маппинге). Финальный гейт — `UvChartQuality.Measure(...).Improves(preMerge, preMerge)`;
  любая неоднозначность — откат к снапшоту. ID чартов после раундов компактируются в 0..N−1.
- **Не трогается:** native ABI и вендоренный xatlas; `originalChartCount` /
  `originalSmallChartCount` (это бейслайн до оптимизаторов); настройки чарта пользователя.
- **Протокол A/B:** сначала простая модель (куб/симметричная), затем регрессия на Playground
  LODGroup; ≥10 повторов; метрики: islands, small islands (≤8 tris), mean/max stretch, время
  стадии Unwrap; baseline — тот же unwrap с выключенным переключателем.
- **GO-критерий:** islands ↓ ≥10% на Playground-наборе при `meanStretch ≤ max(1.15, orig·1.1)`,
  `maxStretch ≤ max(4, orig·1.1)`, 10/10 байт-в-байт детерминизм UV и TransferValidator без overlaps.
- **STOP-критерий:** рост stretch за гейтами, недетерминизм, деградация бейка по швам →
  revert без компенсаций.
- **Статус:** реализация (PR Exp #1); измерения по протоколу — до мержа.
- **Найдено при ручной проверке (краш редактора):** merge-фит мог выставить UV вне [0,1]
  (повёрнутый чарт торчит за атлас), а UvMesh-пакер xatlas требует входные UV в [0,1] —
  `ASSERT texcoord.x >= 0` (xatlas.cpp:8598) и нативный краш в `xatlasPackCharts`. Фикс:
  нормализация всего лейаута одним uniform-масштабом до подачи в пакер + проверка [0,1]
  на выходе. Вторая мина рядом: `xatlasDestroy()` выполнялся до чтения выходов
  (use-after-free); сессия перестроена как create → add → pack → read → destroy.
  **Отдельный краш на фиксе:** нормализация на весь [0,1] делала суммарные extent'ы чартов
  равными атласу, per-chart ceil-округление xatlas добавляло сверху — чарты переставали
  помещаться, пакер выставлял отрицательные координаты и ассертил (тот же xatlas.cpp:8598).
  Фикс — внутренний 4× oversample (resolution × 4, padding × 4) с нормализацией выхода
  бриджем, ровно как в продакшн-repack (`XatlasRepack` internalOversample).
  **Настоящая причина тиражных крашей после нормализации (3 дампа, доказано логом+реплеем):**
  `RunNativePackAsync` рассчитан на вызов с главного потока — внутри `Task.Run(нативный пак)`
  плюс `EditorApplication.timeSinceStartup`/`UvProgress`; с worker-потока стадии Unwrap
  `timeSinceStartup` кидал исключение немедленно, `finally { xatlasDestroy(); }` освобождал
  глобальный атлас под всё ещё работающим паком на пул-потоке → use-after-free, AV-чтения
  с блуждающими адресами внутри `PackCharts`. Реплей точных входов (захват в
  `%TEMP%/meshlab-uvmerge`) нативно НЕ падал — краш был средой, не данными. Фикс: пак
  выполняется инлайн на том же worker-потоке (create → add → pack → read → destroy одним
  потоком, без editor-API и пул-тасков), с бюджетной проверкой стоимости пака.

## Эксперимент 2026-08-06 — Самодостаточная нормализация winding в repack

- **Проблема:** standalone `Repack All` и отключаемый Weld в full pipeline
  позволяли вызвать `RepackSingle`/`RepackMulti` с mirrored UV0 shell.
- **Изменение:** оба repack entry point нормализуют собственные копии UV0
  непосредственно перед передачей данных в xatlas; исходный UV0 mesh не
  изменяется.
- **Ожидание/проверка:** mirrored shell учитывается в `flippedShells`, UV2
  упаковывается в положительном winding, а тест неизменности UV0 продолжает
  проходить.

## Эксперимент 2026-08-06 — безопасные размеры oversampled atlas

- **Проблема:** умножение `resolution × internalOversample` в `uint` и расчёт
  стоимости pack в `long` могли переполниться до вызова native xatlas.
- **Изменение:** размеры вычисляются через `ulong` и отклоняются при выходе за
  диапазон `uint`; стоимость pack насыщается до `long.MaxValue`, чтобы лимит
  всегда отклонял чрезмерный atlas.
- **Ожидание/проверка:** штатные разрешения не меняются; переполняющиеся значения
  завершают repack с ошибкой до native pack.

## Эксперимент 2026-08-06 — Изоляция cross-LOD hints по mesh group

- **Проблема:** индексы source shell локальны для mesh, но overlap/match hints
  накапливались в общих списках и могли влиять на несвязанный mesh с совпавшим
  числовым индексом shell.
- **Изменение:** состояние hints разделено по паре source `MeshEntry` +
  `meshGroupKey`; между LOD передаются только hints той же пары.
- **Сохранение поведения:** внутри одной LOD-цепочки приоритет issues → hint →
  3D distance не меняется; очищение состояния перед новым transfer/auto-tune
  запуском сохранено.
- **Проверка:** требуется ручной Unity-прогон на нескольких mesh groups с
  совпадающими локальными индексами shell и сравнение UV2 на всех LOD.

## Эксперимент 2026-08-06 — Безопасная post-pack коррекция split charts

- **Проблема:** shrink исходного UV shell вокруг общего центроида сдвигал его
  разнесённые xatlas charts через занятые области атласа.
- **Изменение:** плотность и коэффициент по-прежнему считаются на исходный shell,
  но каждый фактический xatlas chart сжимается вокруг собственного центроида.
  Shell с вершинами без надёжного chart ID остаётся без изменений.
- **Ожидание:** chart после коррекции остаётся внутри собственного packed AABB;
  существующее выравнивание плотности и shrink-only поведение сохраняются.

## Правила экспериментов

1. Один PR = одно изменение. Не наслаивать фиксы.
2. Тестировать сначала на простой модели (куб, симметричный объект).
3. Проверять регрессии на сложной модели (Playground LODGroup).
4. Документировать результат здесь ДО мержа.
5. Если ломает — реверт. Не компенсировать другим фиксом.

## Исправление 2026-08-06 — исключительный доступ к native xatlas session

- **Проблема:** async repack оставляет Editor отзывчивым, поэтому параллельный
  вызов мог заменить или уничтожить единственный глобальный `s_atlas`, пока
  background task ещё выполнял native pack.
- **Изменение:** все managed-входы в xatlas session используют общий atomic
  fail-fast guard; повторный вызов отклоняется до `xatlasCreate`, а guard
  освобождается в `finally` после `xatlasDestroy`.
- **Ожидание/проверка:** обычные sync/async repack сохраняют прежний результат;
  перекрывающиеся вызовы больше не входят в native bridge одновременно.

## Эксперимент 2026-08-06 — Линейная фильтрация overlap-ячеек

- **Проблема:** попарная проверка всех face в каждой ячейке сетки имела худший случай
  `O(gridCells * faces²)` и позволяла патологическому UV-мешу надолго блокировать Editor.
- **Изменение:** число face, имеющих общую вершину с текущим face, вычисляется линейно через
  счётчики вершин, пар и троек (формула включений-исключений). Наличие остальных face означает overlap.
- **Сохранённый контракт:** соседние face с общей вершиной игнорируются, а не смежные face в общей
  ячейке по-прежнему помечаются как overlap.
- **Проверка:** добавлены EditMode-тесты для обоих случаев; сложные модели необходимо проверить
  локально в Unity Editor по стандартному протоколу.

## Что НЕ работает (уроки из 5 отклонённых PR и ~10 ревертов)

- **Affine UV0→UV2 mapping** → экстраполяция за пределы шелов → overlap (PR #48, reverted)
- **Включение merged в dedup** → merged вытесняет non-merged → UV2 jumps между LOD (PR #47, reverted)
- **Coverage upgrade to merged** → меняет transfer mode → другие UV2 позиции (PR #47, disabled)
- **Fragment merge sub-grouping по нормалям** → POST-DEDUP DUPLICATE (PR #47, reverted)
- **Centroid matching вместо normal** → 254→380 overlaps (PR #47, reverted)
- **Additive normal penalty** → перевешивает distance для мелких шелов (replaced в PR #51)
- **Per-shell UV0 пороги для merge-detect** → ложные срабатывания (PR #29, #30 — closed)
- **Shape penalty (scale, shear, анизотропия)** для xform vs interp → усложнение без улучшения (PR #34 — closed)
- **Normal-filter fallback с distance порогом** → ломает partition path (PR #28 — closed)
- **Pre-repack UV0 offset** → xatlas игнорирует позицию, только форма (PR #58)
- **Bbox-only overlap detection** → пропускает идентичные позиции SymSplit (PR #58)

## Что работает

- **Clamped barycentrics** > affine mapping — стабильно по конструкции
- **Multiplicative normal penalty** > additive — масштабируется с distance (PR #51)
- **Metadata (symSplitSide)** > алгоритмическое угадывание — 1 int per shell дешевле 5 ревертов
- **Post-hoc iterative fixing** > pre-processing — SymSplit overlap shifting
- **Non-merged приоритет в dedup** — merged shells используют 3D voting, им не нужен specific source
- **Dual overlap detection** (centroid proximity + bbox ratio) — ловит оба типа overlap

## Текущее состояние (v0.15.39)

**Включено:**
- Clamped barycentric interpolation (основной transfer mode)
- Multiplicative normal penalty в FindBestSourceShell
- SymSplit metadata (symSplitAxis, symSplitSide) + reconstruction в transfer
- Dual UV2 overlap detection (centroid + bbox, iterative)
- UV0 perturbation перед xatlas (EPSILON_SCALE)

**Отключено:**
- Coverage upgrade to merged — только диагностика (GroupedShellTransfer.cs:1118)

**Удалено (v0.15.39):**
- BorderRepairAdapter, BorderRepairSolver, SourceMeshAnalyzer
- ShellAssignmentSolver, InitialUvTransferSolver, TransferQualityEvaluator
- Dead Pipeline Settings UI (sourceUv, maxDist, normalAngle, submeshFilter, borderRepair)

## Известные ограничения

1. **Phase 2b dedup** — хрупкая N² логика, iterative rematch может зацикливаться
2. **StripParameterization PCA** — lambda2 может → 0/NaN на вырожденных данных
3. **Пороги не масштабируются** — UV_NEAR, POS_FAR фиксированы, ломаются на нестандартных mesh
4. **Coverage check отключён** — нужен, но при включении меняет transfer mode → UV2 jumps
5. **FindBestSourceShell** — O(N³) worst case без кэширования

## Roadmap

### Фаза 1: Стабилизация (малый риск) ← ТЕКУЩАЯ
- ~~Adaptive thresholds в SymSplit (масштабировать по mesh/UV bounds)~~ — DONE (v0.15.47)
- PCA stability в StripParameterization (clamp lambda2)
- Epsilon harmonization (нормали → 1e-8f)
- EPSILON_SCALE 0.2% → 2%

### Фаза 2: Визуализация (нулевой риск для pipeline)
- Auto-overlay проблем после трансфера
- Summary badge с расшифровкой

### Фаза 3: Dedup (средний риск)
- Anti-deadlock guard, кэширование FindBestSourceShell

### Фаза 4: Coverage (высокий риск, исследование)
- Реактивация coverage без смены transfer mode

## Эксперимент 2026-04-14 — SymSplit shell-to-shell matching в SplitWithParams

- **Проблема:** эвристика `bestShell` (largest overlap shell) в `SplitWithParams` выбирала самый большой overlap shell, из-за чего на LOD2 с несколькими похожими shell split мог применяться не к ожидаемому shell.
- **Изменение:** добавлен явный descriptor-based идентификатор shell в `SplitParams` (signature + UV centroid/size + faceCount + sourceShellId), запись id на source в `Split(mesh, shells, out outParams)` и поиск target shell по exact signature.
- **Fallback:** если exact signature не найден, применяется nearest descriptor distance с `UvtLog.Warn`.
- **Ожидание/проверка:** сценарий «1 source shell → несколько похожих target shells на LOD2» теперь стабильно берёт shell по descriptor id, а не по количеству face.

### Дополнение (2026-04-14, round 2)
- **Расширение состояния source shell:** в `SplitParams` дополнительно сохраняются `descriptor.stableHash`, `uv0Area`, `boundaryLength`, `worldCentroid`, `worldNormal`, `sourceMirrored`, `sourceGroupId`.
- **Связь между LOD:** matching теперь учитывает `descriptorHash` и `groupId` как первичную связь shell→shell/группа→группа, а затем distance fallback.
- **Фикс бинарного кейса:** параметры теперь пишутся по исходным shell (`symSplitSide == 1`), а не по добавленным shell, чтобы не терять связь с source.

## Эксперимент 2026-04-15 — Полные и воспроизводимые параметры SymSplit в `Split(..., out outParams)`

- **Проблема:** бинарный этап запускался только если `totalSplit == 0`, из-за чего shell без N-fold могли остаться без binary split, а логи/параметры были неполными.
- **Изменение 1 (pipeline):** этапы разнесены явно: `Detect+Apply N-fold` → `Detect+Apply Binary` только по shell, не обработанным N-fold.
- **Изменение 2 (threshold):** для binary split записывается фактический `splitThreshold` из midpoint-votes (без принудительного `0f` при малом числе голосов).
- **Изменение 3 (params):** `SplitParams` добавляется для каждого реально применённого split (N-fold и binary) с сохранением source descriptor state.
- **Изменение 4 (диагностика):** итоговый лог теперь печатает breakdown по параметрам: `total`, `N-fold`, `binary`.
- **Ожидание/проверка:** воспроизведение split-паттерна на target LOD детерминированно при смешанном наборе shell (часть N-fold, часть binary).

### Дополнение (2026-04-15, round 2)
- **Логи этапов:** добавлены явные служебные логи старта этапов `Stage 1/2: Detect+Apply N-fold` и `Stage 2/2: Detect+Apply binary on remaining`.
- **Итоговая диагностика:** итоговый лог `Split params` дополнен `applied splits total`, чтобы сверять число реально применённых split с числом сериализованных `SplitParams`.

## Эксперимент 2026-04-15 — Adaptive `UV_NEAR/POS_FAR` для shell matching (Round 3, decision protocol)

- **Статус:** proposal / A-B validation against legacy (до включения по умолчанию).

### 1) Гипотеза

Фиксированные пороги `UV_NEAR/POS_FAR` хуже переносятся между shell разного масштаба.
Ожидаемые улучшения от adaptive-порогов:
- **Symmetry shell:** меньше ошибочных пар left/right при небольшом drift UV/позиции после split/repack.
- **LOD2+ с похожими shell:** выше точность выбора source shell среди близких кандидатов (одинаковый class/group, но разный размер).

### 2) Формула / эвристика adaptive-порогов

Для каждого source shell считаются признаки масштаба:
- `uvArea` — площадь в UV0;
- `boundaryLength` — длина границы в UV0;
- `uvDiag` — диагональ UV0 AABB;
- `posDiag` — диагональ world/object AABB.

Нормировка в пределах текущей группы (устойчиво к outlier через медиану):
- `sUv = clamp(uvDiag / medianUvDiag, 0.5, 2.0)`
- `sPos = clamp(posDiag / medianPosDiag, 0.5, 2.0)`

Расчёт порогов:
- `UV_NEAR_adaptive = UV_NEAR_legacy * lerp(0.85, 1.35, (sUv - 0.5) / 1.5)`
- `POS_FAR_adaptive = POS_FAR_legacy * lerp(0.80, 1.40, (sPos - 0.5) / 1.5)`

Стабилизаторы:
- глобальный clamp: `UV_NEAR ∈ [0.75x, 1.50x]`, `POS_FAR ∈ [0.70x, 1.60x]` от legacy;
- micro-shell guard: если `uvArea < P10`, применить `UV_NEAR *= 0.9`;
- thin-shell guard: если `boundaryLength / sqrt(uvArea) > P90`, не расширять `UV_NEAR` выше `1.15x` legacy.

### 3) Тестовый набор и метрики vs legacy

**Сцены/модели (обязательный минимум):**
1. Симметричный prop (эталон left/right shell).
2. Playground LODGroup (сложный mixed-кейс пакета).
3. LOD-цепочка с 3+ уровнями, где LOD2/LOD3 содержит похожие shell-кандидаты.
4. Stress-кейс с мелкими fragment shell после dedup/merge.

**Протокол:**
- A/B: `legacy` vs `adaptive` на одинаковом входе;
- 10 повторов на кейс (проверка детерминизма);
- фиксировать seed/порядок загрузки для воспроизводимости.

**Метрики:**
- `correct_match_%` — доля shell с ожидаемым source shell id/group id;
- `fallback_count` — число переходов на descriptor-distance fallback;
- `overlap_count` — финальные UV2 overlaps после post-hoc fixing;
- `rerun_stability_%` — совпадение mapping hash между 10 прогонами;
- `matching_time_ms` — среднее время этапа shell matching.

### 4) Stop/Go критерии

**GO (adaptive становится default), если одновременно:**
- `correct_match_%` на symmetry-кейсе не хуже legacy, и минимум `+3%` на LOD2+ похожих shell;
- `fallback_count` не растёт более чем на `+10%` относительно legacy;
- `overlap_count` не выше legacy на всех эталонных сценах;
- `rerun_stability_% >= 99.5%`;
- `matching_time_ms` рост не более `20%`.

**STOP (оставляем/возвращаем legacy), если выполняется любой пункт:**
- деградация `correct_match_%` на symmetry-кейсе более чем на `1%`;
- рост `overlap_count` хотя бы на одной эталонной сцене;
- `rerun_stability_% < 99%` или зафиксирован недетерминизм выбора target shell;
- рост `matching_time_ms > 20%` без подтверждённого quality-win.

**Решение по умолчанию:**
- до выполнения GO-критериев adaptive держать за feature-flag / экспериментальный режим;
- после 2 последовательных прогонов полного набора без регрессий — переводить в default.

---

## Batch 8-tasks (v0.15.47, 2026-04-14)

### Выполнено

1. **Diagnostic logging** — overlap relocator, FBX export, repack pipeline
   - Per-pair shift axis/magnitude/ratio в `FixOverlappingUv2Shells`
   - Rescale UV2 logging
   - FBX export: pruned children, collision count, material trim logging

2. **CountAabbOverlaps metric** (`UvShellExtractor.CountAabbOverlaps`)
   - O(N²) подсчёт пар с bbox overlap > threshold
   - Логируется pre-repack для каждого mesh

3. **SymSplit adaptive thresholds** (`SymmetrySplitShells.cs`)
   - `POS_FAR` = meshDiagonal * 10% (floor 0.1)
   - `UV_NEAR` = shellUvDiagonal * 5% (floor 0.005)
   - Grid search radius масштабируется с uvNear
   - **Требует тестирования на WateringCan и Playground**

4. **ShellTopology iteration cap** (`GroupedShellTransfer.EnforceShellTopologyOnUv2`)
   - Увеличен с 3 до 5 итераций
   - Per-iteration convergence logging
   - Warning если cap достигнут с fixable vertices

5. **Free-space relocator** (`XatlasRepack.RelocateToFreeSpace`)
   - 128x128 occupancy grid из non-overlapping shell AABBs
   - Поиск свободного прямоугольника для каждого overlapping shell
   - Заменяет Phase 2 brute-force all-pairs shift
   - **Требует тестирования: может ли atlas utilization улучшиться?**

6. **N-fold rotational symmetry detection** (diagnostic only)
   - PCA rotation axis detection
   - UV0 layer counting via grid sampling
   - Логирует обнаруженную N-fold symmetry
   - **Не сплитит — только диагностика. Сплит в отдельном PR**

### Не протестировано (требует Unity)

- Все изменения требуют тестирования в Unity Editor
- Порядок тестирования: простая модель → WateringCan → Carousel → Playground
- Если SymSplit adaptive thresholds ломают существующие модели → revert к fixed values
- Если free-space relocator хуже axis-shift → revert к Phase 2 all-pairs

---

## FBX Export & Collision — Known Issues & Constraints

> Обновлено: 2026-04-09

### AssetPostprocessor вызывает массовый реимпорт
- Наличие `OnPreprocessModel` / `OnPostprocessModel` в пакете заставляет Unity реимпортировать ВСЕ модели при установке.
- На больших проектах (тысячи FBX) это ломает collision meshes (0 вершин).
- **Решение**: `OnPostprocessModel` always compiled; controlled at runtime by `PostprocessorDefineManager.IsEnabled()` (EditorPrefs toggle). `OnPreprocessModel` replaced with static `PrepareImportSettings()`. The package is passive on install — postprocessor early-returns when sidecar mode is off and no transient replay is armed.

### Collision mesh (_COL) — нормали обязательны для FBX Exporter
- Unity FBX Exporter (`ModelExporter.ExportObjects`) не может корректно записать mesh без нормалей → 0 вершин после реимпорта.
- **Решение**: `RecalculateNormals()` перед экспортом. Collision mesh в FBX хранит Position + Normal + Tangent.
- Tangent-ы добавляет FBX Importer при реимпорте — безвредно, MeshCollider их игнорирует.
- Убрать нормали/tangent-ы без поломки экспорта нельзя (ограничение Unity FBX Exporter).

### Collision mesh — isReadable = false
- FBX sub-asset meshes по умолчанию не readable.
- `Object.Instantiate(mesh)` НЕ гарантирует readable копию во всех версиях Unity.
- **Решение**: перед экспортом временно включается `isReadable = true` на ModelImporter, с bypass для постпроцессора. Для overwrite пути `.meta` восстанавливается из backup.

### Sidecar collision entries — не удалять при FBX overwrite
- Старый код удалял весь sidecar (включая collision entries) после overwrite.
- При повторном экспорте collision meshes были недоступны (non-readable FBX sub-assets).
- **Решение**: `SidecarStore.ClearUv2Entries()` — удаляет только UV2 entries, сохраняет collision entries.

### Convex hull triangle indices — глобальный offset
- `SaveToSidecar()` хранит triangle indices как flattened array. Для multi-hull convex decomposition индексы должны быть rebased к глобальному vertex offset.
- Без rebasing: hull 1+ получает отрицательные индексы → сломанный mesh.

### Import settings (weldVertices и т.д.) безопасно оставлять
- `PrepareImportSettings()` отключает `weldVertices`, `meshCompression`, `meshOptimizationFlags` для корректной работы UV2 remap.
- Эти настройки сохраняются в `.meta` и **не ломают mesh-и** — просто отключают minor оптимизации.
- НЕ нужно восстанавливать после экспорта. Нюк-кнопка НЕ трогает `.meta`.

### AO данные в UV каналах — PreserveUvChannels
- AO записывается в `originalMesh` (рабочая копия), но экспорт берёт `resultMesh` (repacked/transferred).
- `PreserveUvChannels()` копирует UV каналы из source mesh если export mesh их не имеет.
- **Важно**: копировать из ОБОИХ `fbxMesh` (базовые UV) И `originalMesh` (AO и другие модификации).

### Cleanup — опасные операции
- `FixColliders()` → `mesh.Clear()` стирает ВСЕ атрибуты. Не стрипать если mesh shared с Renderer.
- `FixMeshStripUvs()` — vertex colors `(0,0,0,0)` может быть валидное AO (полная окклюзия). Авто-стрип colors убран.
- `SaveAndReimport()` в cleanup — добавлять `bypassPaths` чтобы постпроцессор не вмешался.

---

## External MeshLab (desktop) — Settings to Avoid Artifacts

> Обновлено: 2026-05-02
>
> Применимо к FBX, прошедшим через десктопный **MeshLab** (https://www.meshlab.net/) перед импортом в Unity. Не путать с пакетом **UnityMeshLab** — это про внешний инструмент.
>
> Программная проверка симптомов: `MeshLabArtifactValidator` (`Editor/MeshLabArtifactValidator.cs`),
> menu **Tools → Mesh Lab → Validators → Check Imported MeshLab Artifacts** (по выделенному GameObject/asset).

### Filters — что ломает геометрию

1. **Merge Close Vertices** — `Distance` ставить очень маленьким (`1e-6`) или отключать filter.
   - На дефолтном threshold склеивает per-wedge UV-вершины в одну → **UV seams разъезжаются**, текстура «плывёт» по швам.
   - Симптом в Unity: `mesh.vertexCount == уникальных позиций`, при том что в сцене заметны видимые швы.

2. **Parameterization (UV1)** — выбирать filter с поддержкой существующих UV seams: `Trivial Per-Triangle Parameterization` или `Iso Parametrization`.
   - Дефолтный `Parameterization: Flat Plane` ломает развёртку UV1 (весь mesh проецируется в одну плоскость).
   - Симптом: UV1 — degenerate triangles или всё в `(0,0)`.

3. **Remove Duplicate Faces / Remove Duplicate Vertices** — отключать, если есть split UVs.
   - В 3ds Max split UVs выглядят как «дубликаты» по позиции (но имеют разные UV/normal).
   - MeshLab их склеивает → теряются UV seams и smoothing groups.

### Export FBX dialog — обязательные настройки

| Опция | Значение | Зачем |
|-------|----------|-------|
| `WedgeAllChannels` | **ON** | Сохраняет per-wedge UV / normal (т.е. split UVs / smoothing seams). |
| `UseExistingNormals` | **ON** | Не пересчитывает нормали (иначе теряются smoothing groups → flat shading). |

### Симптомы плохих настроек в Unity (что ловит валидатор)

- **No UV seams**: `vertexCount` ≈ числу уникальных позиций при наличии нескольких UV-островов → `Merge Close Vertices` или `Remove Duplicate Vertices` склеили wedge.
- **Flat-shaded normals**: > 90% треугольников имеют 3 одинаковых vertex-нормали, совпадающих с face-normal → нормали пересчитаны без сохранения smoothing.
- **Degenerate UV1**: > 5% треугольников UV1 с нулевой площадью — broken parameterization.
- **UV1 all zero**: все UV1 в `(0,0)` — MeshLab стёр UV1 канал.
- **UV1 out of unit**: > 5% UV1-вершин вне `[0..1]` — Flat Plane / Iso без нормализации.

### Проверка

Перед импортом FBX из MeshLab в Unity:
1. Прогнать `Tools → Mesh Lab → Validators → Check Imported MeshLab Artifacts` на корне.
2. Если warning — пересохранить из MeshLab с правильными настройками.
3. Если повторяется — отключить проблемный filter и переэкспортировать.

## Эксперимент 2026-05-13 — Pre-pack snap к integer atlas pixels (отклонено)

**Гипотеза:** xatlas `PackCharts` применяет unconditional per-chart `ceil(extents)` rescale (xatlas.cpp:8345-8362) — sub-pixel-thin шеллы амплифицируются и ломают uniform density. Если pre-snap'ить UV extents per-shell к integer pixel grid до xatlas, ceil() становится no-op'ом и density сохраняется без форка xatlas.

**Что попробовали (5 коммитов, все ревёрнуты):**
1. `SnapShellsToIntegerPixels(uvFlat, shells, tpu)` — per-shell scale вокруг центроида к ceil(extent×tpu)/tpu.
2. tpu = `effectiveTpu = internalRes × sqrt(coverage)`.
3. Добавление `sqrt(s/p)` фактора (ошибочно — для UvMesh xatlas.cpp:8255 хардкодит `surfaceArea = parametricArea`, т.е. `s/p = 1`).
4. Force `rotateCharts = 0` чтобы snap-grid не сбивалась PCA-вращением.
5. Force `resolution = 0` чтобы избежать xatlas's `maxChartSize` clamp (xatlas.cpp:8363-8385).
6. Перестановка `snap` после `PerturbOverlapShellsUv0` (perturb рескейлил шеллы вокруг чужих центроидов и сбивал snap).

**Результаты на Carousel (149 шеллов, 5 overlap групп, max 92 в одной):**

| Этап | `postAssign maxRatio` (density) | `postCorrection maxRatio` |
|---|---|---|
| Без snap (только Normalize + PostPackCorrection) | ~14× | ~2.95× |
| Snap к tpu (исходник) | 23.11× | 3.76× |
| + sqrt(s/p) factor (wrong для UvMesh) | 23.11× | 4.19× |
| + resolution=0, rotateCharts=0 | 23.11× | 4.06× |
| + snap после perturb (вместо до) | 20.22× | 4.06× |

**Вывод:** snap не работает и делает чуть хуже. Причины математически:
- Для anisotropic shell после snap (sx ≠ sy) xatlas's per-chart scale = `1/sqrt(sx × sy)` (потому что parametricArea меняется на `sx × sy`) — частично откатывает наш snap.
- post-xatlas pixel extent для оси x = `original_x × sqrt(sx/sy)` ≠ integer.
- Isotropic snap (sx = sy = s) полностью undone xatlas (scale = 1/s).
- Единственный способ полностью устранить Stage B amplification — форк xatlas или собственный rect-packer.

**Что оставлено:** `TexelDensityNormalizer.Normalize` (uniform au/a3 = const в UV0) + `PostPackDensityCorrection` (shrink-only post-xatlas). Дает стабильный ~3× density spread на Carousel — лучший достижимый без форка/собственного pack'а.

**Файлы удалены/откачены:** `SnapShellsToIntegerPixels`, `ComputeEffectiveTpu` в `XatlasRepack.cs`, `RepackOptions.snapShellsToIntegerPixels`, `UvToolContext.SnapShellsToIntegerPixels`, UI toggle, force `rotateCharts=0`/`resolution=0` ветки в pack call.

**Что НЕ пробовать снова:**
- Per-shell snap к integer pixel grid в любом виде — xatlas's per-chart scale rebuild параметрической площади после snap всё равно ломает grid.
- "Pre-multiply UV by F в pixel space" — эквивалентно snap по математике (xatlas в обоих случаях пересчитает `sqrt(s/p) × tpu` от новой UV-area и обнулит наш масштаб).
- Передача `texelsPerUnit > 0` с `resolution > 0` — триггерит maxChartSize clamp.

**Что МОЖЕТ помочь (не пробовали):**
- Передавать xatlas **готовый layout** через `xatlasAddMesh` с явными chart definitions, обходя его pack-stage rescale целиком.
- Свой 2D rect-packer на C# поверх Normalize-выровненных шеллов.

## Эксперимент 2026-05-13 — Density spread 14× → 1.17× без форка (commit a218a2b)

**Контекст:** после отката pre-pack snap'а (~5 коммитов) вернулись к чистому `Normalize + xatlas + PostPackCorrection`. Density spread держался на ~3× postCorrection, ~14× pre-correction. Цель — закрыть его не форком.

**Что сработало (в комбинации):**
1. **`UvToolContext.InternalOversample = 1 → 4`** (плюс `RepackOptions.Default.internalOversample = 4`). xatlas pack runs at 1024×1024 internal для пользовательских 256. Sub-pixel шеллы исчезают: shell с UV-extent 0.001 при tpu=909 даёт ≈0.91 px = amp 1.1×, не 4×.
2. **`rotateChartsToAxis = false`** в `RepackOptions.Default`. PCA-rotation перед extents сжимала тонкие шеллы и усиливала `ceil()` amp. Для repack existing UVs (UvMesh путь) она бесполезна. `rotateCharts = true` (90° placement) остаётся.
3. **Удаление `PerturbOverlapShellsUv0`** из обоих pack путей. Это был **главный** убийца: для AddUvMesh xatlas НЕ дедупит charts по UV-сходству — он сегментирует faces по `faceMaterial` (= `shellID`) плюс colocal-UV walk через `vertexToChartMap` (xatlas.cpp:6228-6275). Distinct shellID всегда → distinct charts. Perturb scale `1 + g × strength` cumulative по индексу в группе раздувал sumUV в **~97×** на overlap-группе из 92 шеллов (Carousel), что обрушивало xatlas auto-tpu со 256 до ~104 и кидало все шеллы в sub-pixel regime.
4. **Диагностика `[DensityRisk:prePack]`** в `XatlasRepack.LogStageBRisk` — предсказывает per-shell Stage B amplification (`ceil(extent_px)/extent_px` per axis × per axis) до xatlas. Использует тот же `tpu = sqrt(internalRes² × 0.75 / sumUv)` что xatlas считает внутри. Логирует `subPixel count`, `boost>1.5× count`, `boost>3× count`, top-5 worst шеллов. Точная корреляция: predicted `areaBoost=1.34×` ↔ фактический `postAssign maxRatio=1.27×`.

**Результат на Carousel (149 шеллов, 5 overlap групп, max group 92):**

| Метрика | До | После |
|---|---|---|
| Sub-pixel шеллы | 38/149 | 0/149 |
| `[postAssign] maxRatio` | 20-23× | **1.27×** |
| `[postCorrection] maxRatio` | 3-4× | **1.17×** |
| Шеллов внутри ±10% mean | 10/149 | **149/149** |
| Atlas utilization | 28-34% | **55%** |

**Не пробовать снова:**
- `PerturbOverlapShellsUv0` для UvMesh пути — xatlas не дедупит, perturb только ломает sumUV.
- Per-shell pre-pack snap — xatlas пересчитает per-chart scale от изменённой parametricArea и отменит snap (см. предыдущий эксперимент).
- `rotateChartsToAxis = true` для repack existing UVs — мутирует extents.

## Эксперимент 2026-05-13 — Oversample heuristic pack + atlas-scaled UV2 tolerances

**Контекст:** после `a218a2b` default `internalOversample = 4` сохранил density spread, но поднял внутренний xatlas pack с 256² до 1024². Старый preflight отключал brute force только по `shellCount × internalRes² > 500M`; Carousel-кейс 149 × 1024² ≈ 156M оставался ниже budget, хотя wall-time стал ощутимо хуже. В transfer path часть UV2 tolerances оставалась в normalized-space константах (`0.005`, `0.01`), что при resolved atlas 1389×1360 превращало ~1.3px старого допуска в ~6.8px.

**Изменение 1 (repack):**
- `XatlasRepack.ResolvePackBruteForce()` теперь отключает native `bruteForce` при `internalOversample > 1`, даже если stored UI preference включён.
- Старый safety budget остаётся для `internalOversample = 1`; heuristic safety budget по-прежнему запрещает огромные packs.
- UI делает `Brute force pack` недоступным при oversample выше 1× и явно показывает effective packer = heuristic.

**Изменение 2 (transfer):**
- `RepackResult.atlasWidth/atlasHeight` сохраняются в `MeshEntry.repackedAtlasWidth/repackedAtlasHeight`.
- `GroupedShellTransfer.Transfer()` принимает resolved source atlas size и переводит UV2 pixel margins через `pixels / min(atlasW, atlasH)`.
- Legacy fallback остаётся прежним (`0.005`, `0.01`) для source meshes с existing UV2 или неизвестным atlas size.
- Full pipeline теперь явно пропускает transfer/auto-tune, если нет включённых target LOD meshes, вместо трёх source-only repack попыток с `coverage=0%`.

**Проверка:**
- EditMode red/green: `PackPreflight_DisablesBruteForce_WhenInternalOversampleIsAboveOne`.
- EditMode red/green: `BruteForceOption_IsUnavailable_WhenInternalOversampleIsAboveOne`.
- EditMode red/green: `TransferTargetDetection_IgnoresSourceOnlySelection`.
- EditMode red/green: `Uv2PixelMargin_ScalesFromResolvedAtlasSize`.
- Full model benchmark (Carousel/Playground/WateringCan) в этом checkout не прогнан: тестовые FBX/`BenchmarkReports/` отсутствуют в репозитории. Нужен ручной Unity прогон на suite для финального сравнения `repackMs`, `density spread`, `overlapShellPairs`, `invertedCount`, `texelDensityBadCount`.

## Эксперимент 2026-06-03 — Stage D cascade-threshold sweep (4 кейса × 9 ячеек)

**Контекст:** Plan v2 Stage D (cascade group matching deep→fine) родил пороги `cascadeMatchFrac` × `cascadeMinHits`. Чтобы найти knee, прогнали sweep `{0.35,0.50,0.65} × {2,4,8}` на 4 моделях (Gazebo/Carousel/Playground/WoodenBox01). Артефакты — `bench_2026-06-03_00-01-29-061/{case}/hier/stage_d_sweep.csv` + `lod{N}_groups_mf*_mh*.png`.

**Что подтвердилось:**
- Каскадная идея валидна: крупные lighting-домены держат цвет (= один groupId) через все LOD'ы. WoodenBox: пол/задняя стена/правая стена/рама стабильны LOD3→LOD0. Carousel: 6 пирогов канопе и скамьи идентичны LOD2→LOD0 даже при final groupCount=922.
- `missed=0` на каждом переходе во всех кейсах. Проекция здорова, `overlayDistNorm=0.03` не зажат.
- Каскад идёт deepest-first, `reused=0` — родитель всегда резолвится до того, как finer спросит про него.

**Что НЕ работает — главные находки:**
1. **Пороги почти не влияют на качество доменов.** Визуальная сетка 3×3 на Carousel LOD0 идентична на крупных поверхностях во всех 9 ячейках. Разница 808↔1198 групп — целиком в мелких шеллах (тонкая рама, проволока, кромка). Тюнить mf/mh ради качества доменов смысла мало.
2. **Взрыв групп идёт НЕ от плохого матчинга, а от пролиферации микро-шеллов в Stage C.** Carousel LOD0 = 773 raw shells ≈ 6 крупных кусков канопе + ~767 тонких деталей. Поэтому 269→922, Playground 318→1664. И `skipAreaFrac`/`skipMaxFaceCount` тогда были объявлены в `Options.Default`, но **нигде не применялись**.
3. **`minHits` работал контр-продуктивно.** Это был доминирующий рычаг (Carousel LOD1→0 fresh при mh=2/4/8: 421/467/618). Логика «мало хитов → не доверяем голосу → fresh» наказывала именно те крошечные шеллы, которым отдельный домен нужен в последнюю очередь.
4. **`groupCount` — обманчивая метрика.** Она зависит от шума микро-шеллов, не от качества доменов. Минимизировать её = минимизировать фрагментацию проволоки.

**Изменение:** Stage D получил **tiny-shell merge ветку** (commit на бранче `claude/fix-transfer-bugs-KYVQD`):
- finer shell с `totalArea ≤ opts.skipAreaFrac × totalFineArea` И `faceCount ≤ opts.skipMaxFaceCount` force-join'ится на доминантного родителя независимо от matchedFrac/minHits, если bestProxy резолвится.
- Tiny shell без матча (`bestProxy<0` — деталь которой действительно нет на deeper LOD) всё ещё открывает fresh, но засчитывается в отдельный счётчик `tinyOrphan` для видимости. Topological-neighbour fallback для этого кейса не делаем — отложен до данных, показывающих что он нужен.
- `CascadeStat` расширен: `tinyJoined`, `tinyOrphan`. CSV свипа добавляет 2 колонки. Лог Stage D печатает их.

**НЕ сделано / открытые вопросы:**
- Scored auto-winner свипа не строим: до Stage E (final pack + per-LOD UV2 + lightmap-defect счётчики трансфера) нет объективного скаляра для оптимизации `(matchFrac, minHits, skipAreaFrac, skipMaxFaceCount)`. Свип остаётся сравнительным (PNG + CSV под глаза).
- Не нормализовали пороги по `meshDiag`: knee может смещаться между кейсами. Подтверждено в свипе для Playground (LOD3→2 join только 25% против 75% на LOD1→0) — но это геометрическая реальность (LOD3 беднее деталью), не порог.
- Default `skipAreaFrac=0.001`/`skipMaxFaceCount=4` оставлен как есть — ожидаем повторного свипа после tiny-merge, теперь уже по `tinyJoined`/`tinyOrphan`, чтобы калибровать.

**Проверка:**
- Свип-прогон 4 кейса × 9 ячеек завершился без ошибок, артефакты на месте.
- Повторный свип после tiny-merge ещё не делался — TODO следующим шагом, на тех же 4 кейсах, чтобы померить просадку `fresh` и убедиться что `tinyOrphan` мал.

## Эксперимент 2026-06-03 (продолжение) — повторный свип ПОСЛЕ tiny-merge

**Артефакты:** `bench_2026-06-03_01-02-41-045/{case}/hier/stage_d_sweep.csv` (+ per-cell PNG; это прогон до suppress-фикса). Все 4 CSV содержат заполненные `tinyJoined`/`tinyOrphan` → tiny-merge активен.

**Результат на default-ячейке (mf0.50 / mh4), fresh суммирован по переходам:**

| Кейс | seed | groupCount | fresh | tinyJoined | tinyOrphan | orph % от fresh |
|---|---|---|---|---|---|---|
| Gazebo | 138 | 222 | 84 | 59 | 61 | 73% |
| Carousel | 269 | 818 | 549 | 104 | 468 | **85%** |
| Playground | 318 | 1255 | 937 | 409 | 684 | 73% |
| WoodenBox01 | 6 | 105 | 99 | 66 | 97 | **98%** |

**Что подтвердилось:**
1. **tiny-merge делает свою работу.** `tinyJoined` поглощает штраф minHits: 59/104/409/66 шеллов, которые иначе ушли бы в fresh, теперь подсасываются к родителю. groupCount просел против pre-merge свипа (Carousel 922→818, Playground 1664→1255, Gazebo 281→222, WoodenBox 171→105).
2. **Каскад по-прежнему держит крупные домены.** Визуал `lod0_groups.png`: Carousel — 6 чистых секторов канопе + когерентные скамьи; WoodenBox — пол/стены схлопнулись в 3 цвета. `missed=0` везде.

**Что НЕ работает — решающая находка:**
3. **73–98% всех fresh-групп = `tinyOrphan`** — крошечные шеллы (area ≤ 0.001×total И faceCount ≤ 4) у которых `bestProxy < 0`, т.е. на deeper LOD геометрии под них вообще нет. tiny-merge их не трогает (force-join требует резолвящегося родителя), поэтому каждый открывает свой lighting-домен. Это и есть оставшийся источник взрыва групп — тонкий обод, трубки ножек, кромки рамы (визуально: разноцветное крошево по ободу Carousel и по рамам WoodenBox).
4. **Порог тут бессилен — это доказано данными, не на глаз.** `tinyOrphan` на финишном переходе LOD→0 имеет РОВНО ОДНО значение через все 9 ячеек: Gazebo=19, Carousel=378, Playground=83, WoodenBox=86. Он threshold-invariant by construction (`bestProxy<0`), поэтому никакой mf/mh не уберёт доминирующую часть взрыва.
5. **WoodenBox — чистейшая демонстрация:** seed всего 6 групп, финал 105, и 97 из 99 fresh — tinyOrphan. Весь его group-count это orphan-крошево на LOD0.

**Вывод / следующий шаг (ранее отложенный — теперь данные требуют его):** нужен **topological-neighbour fallback** для tinyOrphan. Раньше в коде стоял комментарий «defer until the data shows it's needed» — данные показали: на финишных LOD'ах orphan'ы дают 85–98% fresh. План: построить shell-shell adjacency на finer LOD (по общим рёбрам `canonicalTris`), и tinyOrphan вливать в соседний шелл с наибольшей общей границей, наследуя его groupId, вместо открытия fresh-группы. Открытый вопрос — что делать с orphan'ом, у которого ВСЕ соседи тоже tinyOrphan (цепочка крошечных шеллов): либо chain-resolve до первого не-tiny, либо слить такой кластер в один общий домен.

**НЕ сделано:**
- Topological fallback ещё не реализован — отложен (см. ниже: сначала Stage E даёт объективную метрику).
- Defaults `skipAreaFrac=0.001`/`skipMaxFaceCount=4` не трогали; калибровать уже по метрикам Stage E, а не по `tinyOrphan`.

## Эксперимент 2026-06-03 (Stage E) — старт пакинга домен-атласа, слайс E1

**Решение:** вместо tinyOrphan-fallback идём в Stage E. Обоснование: `groupCount`/`tinyOrphan` — это 3D-сегментация без единой UV-координаты, объективной метрики нет. Только Stage E даёт 2D-атлас → overlap/density/inverted. И вероятно orphan-крошево в атласе займёт пренебрежимо мало — решим это уже по метрикам.

**Нарезка (один концерн = один коммит):**
- **E1 (этот коммит):** `PackDomainCharts` — для каждой lighting-группы её canonical-шелл проецируется планарно (`basisU/V`, `extentU/V` из Stage C) в локальный [0,1], скармливается в `xatlasAddUvMesh` с `faceMaterial = groupId` (границы чартов по группам), `ComputeCharts`+`PackCharts` раскладывают. Выход: `r.domainAtlasRects[groupId]` (общий layout домена) + `domains_atlas.png`. Без записи мешей.
- **E2:** для каждого LOD member-шеллы проецируются в rect своей группы → `finalUv2/finalTris/finalSourceVertexIdx` → `BuildFinalMeshes` (Stage F готов) → `lodN_final_uv2.png`.
- **E3:** метрики на атласе (overlap-пары / inverted / texel density) → CSV + скаляр для пере-свипа порогов.

**Технические находки при реализации E1:**
- `xatlasAddUvMesh → ComputeCharts → PackCharts` — рабочая последовательность в этой кодовой базе (так делает `XatlasRepack.RepackSingle`); `ComputeCharts` на UV-меше НЕ пере-развёртывает, держит наши UV, а `faceMaterial` задаёт границы чартов.
- Выход `xatlasGetOutputVertexData` уже нормирован в [0,1] в этой нативной сборке — проверено по `proxy_uv2_auto.png` (читает выход напрямую, заполняет 0-1 box). Комментарий «atlas-pixel space» в `XatlasRepack.cs` устарел.

**Проверка:** Unity-компиляция в этом окружении недоступна (нет toolchain'а; тесты/FBX/бенч — ручной прогон). Сделана статическая сверка сигнатур (`xatlasAddUvMesh`/`PackCharts`/`GetOutputVertexData`), полей `Shell3D`/`Options`, баланс скобок. Прогон бенча на 4 кейсах + визуальная проверка `domains_atlas.png` — следующий шаг (ручной, в Unity).

### Результаты E1 (bench_2026-06-03_01-48-52-098, 4 кейса)

`domains_atlas.png` сгенерился во всех кейсах, Stage E отработал.

**Что ✅ работает:**
- Пакинг корректен на всех 4 кейсах: UV в [0,1], атлас плотно заполнен, без грубых меж-чартовых наложений. Data-model (groups → canonical charts → packed rects) валидирован end-to-end.
- Крупные lighting-домены = аккуратные прямоугольные чарты (Gazebo — почти идеальная сетка квадов; WoodenBox панели; Carousel секторы канопе; Playground платформы).
- Orphan-крошево, хоть его сотни (Carousel ~468), индивидуально занимает крошечную площадь — крупные домены доминируют по площади атласа. Это подтверждает, что отложить tinyOrphan-fallback было правильно: в атласе оно дешёвое.

**Что ⚠️ вскрылось — главная находка E1: фолдинг кривых шеллов.**
- Шеллы, которые **заворачиваются** (цилиндры, кольца, трубы, дуги: обод Carousel, балясины/арки Gazebo, трубы горок Playground), при планарной проекции на ОДНУ плоскость складываются сами в себя. Визуально: синусоиды (∿∿∿), плотная радужная вертикальная штриховка, «бабочки»-песочные часы, круглые розетки, C-образные завитки.
- **Корень:** `dominantNormal` = нормируемая area-weighted сумма нормалей граней (`ExtractShells`, ~813). У заворачивающегося шелла противоположные нормали взаимогасятся → `dominantNormal ≈ 0` → `ComputePlaneBasis` даёт мусорный базис → проекция вырождается/складывается. Такой чарт = внутренние наложения UV → запёкся бы мусорный лайтмап.
- **Доля невелика** и это тонкий кривой trim (по площади ≪ плоских доменов): Gazebo ~5-6 дефектных из ~280, Carousel — весь обод (заметно больше), Playground — трубы/перекладины по краю. Многие из них — те же tinyOrphan.
- **Метрика детекции (дешёвая, на этапе extraction):** `coherence = |accumNormal| / totalArea` — ≈1 для плоского шелла, →0 для заворачивающегося. Порог отделяет developable-патчи от закрученных.

## Эксперимент 2026-06-03 (Stage E2) — собственно КАСКАД: проекция LOD'ов в rect домена

**Разбор ошибки.** E1 и попытка «стичить UV0» строили Stage E как «развернуть+упаковать каждый canonical-шелл по отдельности» и спорили про параметризацию (planar vs UV0 vs фолдинг). Это **игнорировало весь смысл каскада**. Каскад — про КРОСС-LOD согласованность:
- canonical-шелл группы = самый глубокий член = тот, что **не нашёл мэтч** → **перепаковывается** (получает rect в атласе);
- **все остальные члены на всех LOD проецируются в ТОТ ЖЕ rect** (basis/centroid/extent canonical'а) → один лайтмап валиден через все LOD;
- шелл, не нашедший мэтч на своём LOD → canonical своей fresh-группы → перепакован в свой слот.

UV0/фолдинг — побочный вопрос; параметризация canonical'а вторична, главное — проекция членов в общий rect.

**Изменение (slice E2):**
- Откатил UV0 в `PackDomainCharts` — canonical снова планарный. `rotateCharts:0` ⇒ placed rect = входной [0,1] box, масштабированный/сдвинутый, значит проекцию можно воспроизвести линейным `[0,1]→rect`, и члены лягут в совпадение с canonical'ом.
- Новый `BuildCascadedUv2`: для КАЖДОГО шелла КАЖДОГО LOD проекция верт на плоскость canonical'а его группы → local [0,1] → в `domainAtlasRects[gid]`. Пишет `finalUv2/finalTris/finalSourceVertexIdx` per LOD (без дедупа, по 3 верта на грань — Stage F копирует атрибуты по `srcIdx`).
- `lod{N}_final_uv2.png` — диагностика: один домен должен занимать одну область атласа на всех LOD.

**Проверка:** Unity-компиляция недоступна (нет toolchain'а), статическая сверка типов/сигнатур/скобок. Прогон бенча + `lod{N}_final_uv2.png` (проверить совпадение области домена через LOD) — следующий ручной шаг.

**Открыто:** фолдинг кривых canonical'ов даёт искажённую (но кросс-LOD согласованную) проекцию; barycentric-pull на меше canonical'а вместо планарной плоскости — потенциальное улучшение качества, не блокер каскада.

### Результаты E2 (bench_2026-06-03_02-59-26-442) — каскад ПОДТВЕРЖДЁН ✅

`lod{N}_final_uv2.png` сгенерились на всех LOD всех кейсов. Главная проверка — кросс-LOD согласованность — пройдена.

- **WoodenBox LOD3→LOD2→LOD0:** крупные домены (пол, стены) стоят в ОДНОЙ И ТОЙ ЖЕ области атласа на всех LOD. LOD3 = ~6 чартов в конкретных rect'ах; LOD2 = те же rect'ы, дробятся на больше суб-чартов; LOD0 = те же rect'ы заняты + сотни мелких чартов трима по остатку атласа. Домен заякорен на месте, детализация растёт внутри/вокруг — by construction (шелл группы `g` на любом LOD → `domainAtlasRects[g]`), и визуал подтверждает что багов нет.
- Один лайтмап, запечённый для домена, теперь валиден через все LOD — основная цель каскада достигнута.

**Остаточный дефект (известный, не блокер):** кривые/вырожденные canonical-шеллы дают «Union Jack» чарты с белой штриховкой (внутренние наложения от планарного фолдинга). Согласованы через LOD (одинаковое искажение), но внутри чарта UV перекрываются → этот домен запечётся с артефактом. Кандидаты на фикс: barycentric-pull на меше canonical'а, либо splitting кривых шеллов на developable-патчи.

**Следующее:** либо (E3) метрики на атласе (overlap/density/inverted) для объективной оценки, либо фикс фолдинга кривых canonical'ов. Каскадный костяк готов.

## Эксперимент 2026-06-03 (Stage E fix) — убрана дисторшн-нормировка, единый тексель

**Разбор ошибки (критичной).** Я нормировал каждый шелл в свой `[0,1]` по его `extentU/extentV`. Это (а) **искажало** непрямоугольные шеллы (длинный тонкий → растянут в квадрат) и (б) **убивало тексель-density**: огромная стена и крошечный винт оба → `[0,1]`, т.е. у мелкого текселей на единицу площади в сотни раз больше. Для лайтмапа недопустимо.

**Рецепт (от пользователя): классика → выровнять UV → идентичный тексель.** Реализовано:
- **Реальные пропорции:** canonical-шелл проецируется на свою плоскость в МИРОВЫХ единицах (`inU=dot(d,basisU)`, `inV=dot(d,basisV)`), БЕЗ нормировки-в-квадрат.
- **Идентичный тексель:** xatlas пакует с ФИКСИРОВАННЫМ `texelsPerUnit` (= `atlasRes·√(packEff/totalCanonArea)`), а не auto-fit — все чарты одной плотности.
- **Выравнивание (affine):** из placed-UV canonical'а LSQ-фитом снимается аффинное `inU→atlasU, inV→atlasV` → `r.domainPlacements[gid]`. Canonical воспроизводит своё размещение, а ВСЕ члены группы на всех LOD используют ТОТ ЖЕ affine → лежат в той же области атласа с той же плотностью. Заменило `[0,1]→rect` нормировку.

**Остаётся:** «классика» пока = планарная проекция, что для ПЛОСКИХ шеллов точно (плоскость разворачивается тривиально), но кривые canonical'ы (обод/трубы) всё ещё складываются. Полный фикс — xatlas classical unwrap кривых шеллов (Stage B `fineClassicalUv2` уже считает классику per-LOD) вместо планара. Дисторшн+тексель для плоских доменов теперь корректны.

**Проверка:** Unity-компиляция недоступна, статическая сверка типов/LSQ/единиц/скобок. Прогон бенча — следующий шаг.

## Эксперимент 2026-06-03 (Stage E fix 2) — СОХРАНЯТЬ shell, не перепроецировать

**Разбор (ещё одна моя ошибка).** Даже с реальными пропорциями я всё равно ВЫВОДИЛ UV заново через `dot(d,basis)` (планарная проекция) — т.е. уничтожал исходную развёртку шелла и складывал кривые. Правильно: **сохранять shell** — взять родную UV0 шелла и **проецировать/размещать** её, ничего не перепридумывая.

**Реализация:**
- В `PackDomainCharts` вход xatlas = **сохранённая UV0** canonical-шелла, только recenter (вычесть центроид острова) + единый масштаб `S = √(area3D/areaUV0)` (форма не трогается, лишь нормируется тексель-density: UV-площадь → 3D-площадь). Фикс `texelsPerUnit` → идентичный тексель по всем доменам.
- Affine `scaled-UV0 → atlas` снимается LSQ; в `domainPlacements[gid]` теперь `{uvc, scale, su,ou,sv,ov}`.
- В `BuildCascadedUv2` каждый шелл берёт **свою UV0**, применяет placement своей группы: `in=(uv0−uvc)·scale; uv=affine(in)`. LOD'ы домена делят UV0-раскладку → одинаковый UV0 → одинаковый тексель атласа на всех LOD. Развёртка шелла сохранена, без планарной перепроекции.

Планарный `dot(d,basis)` полностью убран из Stage E. Допущение: LOD'ы делят UV0-layout одного домена (стандартная практика). Если нет — выравнивание матчей сломается, увидим на `lod{N}_final_uv2.png`.

**Проверка:** Unity-компиляция недоступна, статическая сверка типов/границ/скобок (защита от рассинхрона faceMat↔индексы и обрезанных треугольников). Прогон — следующий шаг.

### Результаты preserve-UV0 (bench_2026-06-03_08-41-51-481) — ✅ всё сошлось

Скомпилировалось (после фикса CS0136 `fc`). Картинки по 4 кейсам:
- **Фолдинг ушёл.** `domains_atlas.png`: чарты — реальные UV0-острова с настоящими пропорциями (длинные тонкие = рейки/балясины беседки, веера = секторы канопе Carousel, квадраты = панели). Синусоид/бабочек/Union-Jack больше нет.
- **Кросс-LOD согласованность держится.** WoodenBox LOD3 = 6 крупных доменов блоком 2×3 внизу-слева; LOD0 = ТЕ ЖЕ квадраты в тех же местах + тонкий трим вокруг. Один домен → одна область атласа на всех LOD.
- **Развёртка сохранена** (UV0, не перепроецирована), **тексель единый** (S-нормировка + фикс texelsPerUnit).

Итог: рецепт «сохранять shell → проецировать → выровнять → идентичный тексель» реализован и подтверждён визуально.

**Остаточные мелочи (не блокеры):** packing ~50% (packEff=0.5, можно поднять); тонкий трим — очень тонкие полоски (реальная геометрия); Carousel плотный/шумный (сотни тонких деталей). Допущение про общий UV0-layout LOD'ов подтвердилось на тест-сьюте (домены сошлись).

## Эксперимент 2026-06-11 — Stage E3 метрики + устойчивость placement'ов (E2)

**Цель:** «рабочий надёжный вариант» — у пайплайна не было объективного скаляра качества (всё по PNG на глаз) и было два тихих способа потерять геометрию/раскладку на реальных ассетах.

**Фикс 1 (E1, LSQ degenerate axis).** Канонический чарт — идеально прямая axis-aligned полоска в UV0 → нулевая дисперсия по одной оси → знаменатель per-axis LSQ ≈ 0 → placement всей группы invalid. Теперь вырожденная ось заимствует масштаб разрешённой оси (xatlas применяет uniform scale, rotateCharts:0, флипа нет); обе оси вырождены (чарт-точка) → фолбэк на расчётный `texelsPerUnit/resolution` с якорем в среднем placed-UV.

**Фикс 2 (E2, никогда не дропать грани).** Шеллы групп без валидного placement раньше молча пропускались в `BuildCascadedUv2` → дыры в финальных мешах после Apply. Теперь они эмитятся с uv2=(0,0) (плохой бейк на этих гранях, но не потерянный треугольник), считаются (`finalUnplacedFaces`) и логируются Warn'ом.

**E3 (`ComputeStageEMetrics`)** — растр финального UV2 каждого LOD на разрешении атласа (texel-centre ownership):
- `overlapTexels`/`overlapShellPairs` — тексели, на которые претендуют 2+ треугольника, не являющиеся одной гранью и не seam-adjacent внутри одного шелла (общий source-вертекс или совпадающая UV-вершина в пределах 0.75 текселя). Кросс-шелл конфликты считаются ВСЕГДА: mirror-reuse UV0 между шеллами — ровно тот дефект, который надо ловить (у зеркальных островов все вершины совпадают — нельзя вайтлистить по совпадению вершин). Прислонившиеся острова дают ~1px линии — на порядки меньше площадных настоящих overlap'ов.
- `invertedFaces`/`degenUvFaces`/`oobVerts` — перевёрнутая намотка, нулевая UV-площадь, вершины вне [0,1].
- `tpuMean/P1/P99/Spread` — area-weighted разброс texels-per-world-unit; рецепт preserve-UV0 + фикс texelsPerUnit должен держать ~1×.
- `xLodContainedPct`/`misalignedGroups` — КОНТРАКТ КАСКАДА ИЗМЕРЯЕТСЯ, А НЕ ПРЕДПОЛАГАЕТСЯ: тексели групп, чей canonical на другом LOD, должны попадать в 3×3-дилатированный футпринт canonical-LOD той же группы. Низкий containment = ассет нарушает допущение «LOD'ы делят UV0-layout» → один бейк НЕ валиден через LOD'ы. Это главный флаг надёжности на произвольных пользовательских моделях.

**Выходы:** `stage_e_metrics.csv` (строка на LOD) + `lod{N}_overlap.png` (серый — покрыто, красный — overlap) в bench-папке `hier/`; per-LOD строки в логе; диалог Apply показывает хедлайн (overlap px / unplaced faces / misaligned domains). `stage_d_sweep.csv` получил cell-level колонки `e3OverlapTexels/e3OverlapPct/e3TpuSpreadMax/e3XLodMinPct/e3MisalignedGroups/e3UnplacedFaces` — свип порогов Stage D теперь скорируемый (минимизировать e3OverlapTexels при e3XLodMinPct близком к 100).

**Известные слепые пятна метрики (задокументированы в коде):** одиночный треугольник, сложенный ровно на своего edge-соседа, проходит как adjacent (фолды глубже одного треугольника ловятся через не-смежные пары); же-шелловые острова, разорванные в UV0 на несколько кусков, не конфликтуют (они в разных местах атласа — и это корректно).

**Проверка:** Unity-компиляция в окружении недоступна; статическая сверка типов/скобок/сигнатур. Следующий ручной шаг — бенч на 4 кейсах: ожидаем `overlapPct` ≈ 0 на WoodenBox/Gazebo, заметный на Carousel если остался mirror-reuse; `tpuSpread` ~1–1.5×; `xLodContainedPct` > 85% на всём сьюте; `unplacedFaces` = 0.

## Эксперимент 2026-08-06 — Apply UV2 после source-only repack

**Проблема:** оптимизация source-only pipeline корректно пропускала transfer, но оставляла `HasTransfer = false`. Из-за общего UI-gate результат repack нельзя было применить к FBX.

**Изменение:** Quality Report и Validation Overlay по-прежнему требуют завершённого transfer, а Apply/Reset actions теперь доступны после repack или transfer. Transfer и auto-tune для source-only workflow остаются пропущенными.

**Проверка:** EditMode-тест `ApplyUv2_IsAvailable_AfterSourceOnlyRepack` фиксирует доступность Apply при `HasRepack = true` и `HasTransfer = false`.

## 2026-10-13 — PR225 continuation: isolated model space and Kamaz FBX round trip

The placed Kamaz failed its final Reverse UV metric on LOD1 face 9374 before
reaching FBX save. Reverse preparation and final validation used renderer world
matrices while the canvas used model coordinates. A scene rotation and translation
rounded an extremely thin, valid face differently in the two paths.

- Reverse input, detached forward preparation, forward quality checks and diagnostic
  captures now share `ctx.PreviewMatrix`: compose child-local transforms directly
  up to the selected root, excluding the root and scene ancestors. Do not multiply
  a world matrix and its inverse to recover local geometry.
- Remesh, Foliage and Texture AO capture the same isolated model frame. Remesh
  closure previews, projected region areas and filtered-donor rebasing use local
  node transforms. Scene overlays still apply scene placement when drawing into
  the actual Scene view. Source transforms and FBX assets are not reset in place.
- An untouched collision mesh carrying UVs/colours no longer blocks a whole-file
  UV save. Explicit collision replacement/rebuild still strips surface channels.
- Reimport also erased nearby distinct UV1 values, with compression and importer
  welding disabled. UV0 could similarly collapse when UV1 was equal. The raw FBX
  retained the values, so a temporary-document topology edit now duplicates the
  affected control-point identities without moving any corner or UV. Per-point
  channels, edge layers, skin weights and BlendShape deltas follow these identities.
  Unsupported deformation caches refuse the temporary write before publication.
- Whole-geometry FBX replacement previously discarded nonzero-area triangles below
  a relative thickness threshold. It now removes only zero-area polygons, using a
  rebased double-precision area sum. This restores thin authored Kamaz faces.

Focused checks in `_results~/kamaz-reverse-save-20261013/`: local frames retain
thin-face coordinates under a 100 km scene offset, rotation, negative scale and
zero root scale; nested part placement remains intact. Small UV seam round trips
pass at scales 1, 0.001 and 0.00001. Captured exclusive Reverse outputs for all 15
Kamaz meshes survive whole-document write/reimport with bit-identical distinct
position/UV0/UV1 corner tuples. A skinned fixture preserves every bone influence
and BlendShape displacement after the control-point split. These are save/space
regressions, not a claim that Reverse correspondence or packing is optimal.

Final validation on the merged `0ce923b4` base:
- `final-verified/results.xml`: 891 passed, 0 failed, 25 optional fixture tests
  skipped. Includes forward/reverse UV, viewport LOD switching, Remesh hierarchy,
  closure regions, Foliage, LOD small-part selection and FBX document/channel tests.
- Both complete Kamaz workflows passed: Reverse UV with and without projected
  overlap, shared whole-document overwrite, synchronous reimport of all 15 meshes.
  Distinct position/UV0/UV1 corner tuples match exactly, triangle counts and the
  original collider are retained, and original FBX/meta bytes are unchanged.
- A selected Foliage LOD child retains sibling placement even under a zero-scale,
  rotated scene ancestor. Small-seam protection requires proximity in both UV
  channels, so ordinary widely separated UV1 seams do not duplicate control points.
- The C# compile check passes with and without `LIGHTMAP_UV_TOOL_FBX_EXPORTER`;
  metadata coverage and the undeclared-identifier check have no findings.

## 2026-10-12 — PR225 continuation: frozen forward islands, reverse cascade, fresh final padding

The exclusive reverse path now follows the requested order: run ordinary Transfer
on detached copies, freeze its whole island membership, prepare and cut only the
coarsest foundation, match complete finer islands, and repack completed ancestry
families together. The existing overlap path is a control and is unchanged.

- Correspondence uses face hits as proposals for a whole-island similarity fit.
  A partial fit can anchor the complete island; unsupported faces are extensions
  of that group. Several finer islands may share one coarser group.
- A genuinely unmatched island creates a new group and becomes a donor on the
  following step. Invalid input islands are repaired as complete components;
  correspondence is retried after repair before declaring them new.
- Coarsest O/U/C/H/T foundation cuts use existing edges. Finer islands are never
  seam-cut or subdivided. All captured model outputs have zero split source faces.
- Each group keeps precise local UV coordinates until final placement. Large
  temporary canvas offsets previously rounded tiny UV edges before packing.
- After correspondence, normalize each complete family to the requested minimum
  density, using its coarsest member as the reference. Start classic xatlas
  Repack from these families, with fresh padding in final pixel units.
- Without Fill atlas, keep the requested density. With Fill atlas, compare fresh
  native placements at common density multipliers 1/2/3/4/6/8. Every trial applies
  the configured padding anew; no packed atlas or old gutter is scaled afterward.
- Packing proxy union shards below native's face-area threshold now receive a
  local 0.01-pixel guard. They no longer force the whole family into a convex
  hull or rectangle, which previously erased rings and concavities.
- Final packing compares axis-aligned and orthogonal whole-family placements;
  failure of arbitrary-angle placement does not cancel the orthogonal trial.
- Shared vertices of physically subpixel faces can use the existing 0.01-pixel
  Float32 repair budget, without cuts or new triangles. Visible faces retain the
  absolute stretch limit. Every complete family stays connected and its final
  motion residual is independently checked against that budget.
- Native's actual canvas extent is retained, as in ordinary Repack. Rounding it
  to the next power of two previously added a blank quadrant after dense packing.
- Disconnected families are not stitched on the basis of incidental old UV
  coincidence. Geometry, overlap and precision are audited before publication.

Private Unity comparison: `_results~/reverse-frozen-cascade-20261012/models-11`.
All three models completed ordinary / new exclusive / old overlap pipelines.
Settings: minimum 32 tex/m, Fill atlas on, final padding 2 px.

| Model | Final families | Chosen tex/m | Atlas | LOD0 bbox fill | LOD0 stretch P95 |
|---|---:|---:|---:|---:|---:|
| Cafe_Table_A_01 | 160 | 256 | 832 | 49.8% | 1.210 |
| Modern_Kitchen_A | 910 | 128 | 729 | 48.6% | 1.100 |
| Kamaz_Typhoon | 2606 | 256 | 4514 | 67.9% | 1.064 |

The rejected previous screenshots had bbox fill 43.6% / 36.1% / 47.5%,
respectively. Ordinary Transfer gives 39.4% for Kitchen and 57.5% for Kamaz.

Independent exported-layout audit: zero changed geometric triangles, zero
invalid/collapsed faces, zero unexpected overlaps, zero finer healthy-island
splits. All 5,122 healthy finer islands retain a common similarity transform per
island. Maximum completed-family motion residual is 0.004903 bake pixel. Ordinary
Transfer and old overlap values match the preceding published controls after
excluding the newly added, empty whole-match refusal diagnostic field.
The audit separately records whole-component repairs (82 Kitchen and 109 Kamaz
finer islands), rather than calling repaired input shapes preserved originals.

Final focused Unity regression run with a graphics device: 521 passed, 0 failed,
35 optional checks skipped (UV plus incoming main texture-packer coverage). A preceding `-nographics` run failed two lightmap
shader-readback checks; both pass with graphics enabled. After merging concurrent PR225 foliage commits through b90c26c5, the expanded
UV / Texture Packer / Hub / Foliage / FBX run passes 758 tests with 0 failures
and 36 optional skips; both FBX compilation variants pass again. The earlier full-suite
run also found a separate existing skinned Remesh preview scale failure, outside
this UV change; it is not claimed as a passing full-suite gate.

The new analytic checks cover partial anchors, multiple finer islands in one
coarse group, new intermediate donors, complete-island repair/rematch, O/U/C/H/T
foundation cuts, strict density with padding 1/4/8, fresh fixed pixel padding
when Fill atlas increases density, microscopic shared-face stability, concave
mask preservation, whole-family axis rotation, actual canvas normalization,
approximate panel correspondence, incompatible island refusal, sibling overlap
protection and best-orientation fitting of a thin mirrored island.
Both FBX compilation variants pass. `Tools~/ReverseFootprintAudit.cs` is a private
Unity driver that exports the exact native packing proxies from captured groups;
it makes convex-mask inflation inspectable separately from correspondence.

Ghost reservation investigation (same experiment, after merging main 6ca6976d):

- The two large side-panel reservations were foundation charts 5513 and 5531.
  Their six-face LOD1 children were rejected at 0.3126 seed pixels; the roof
  child (21 faces, 12.33 m²) was rejected at 0.5232 pixels from chart 5438.
  Matching used the 0.25-pixel seam tolerance for approximate LOD correspondence.
  The finer panels were born as independent groups while coarse panels still
  reserved their full masks, leaving the visible LOD0 vacancies.
- Whole-island fitting now uses max(seam tolerance, projection reach × seed
  density). Nearest-donor world distance/normal tests, whole-island distortion,
  topology and same-LOD sibling contact audits retain their independent gates.
  Both orientation signs are evaluated and the lowest-residual fit is chosen;
  accepting the first sign under the wider budget misplaces thin mirrored charts.
- Large panel and roof descendants now share their foundation families. Kamaz
  LOD2 aligned area rises from 53.3% to 93.8%; LOD1 from 86.4% to 96.7%.
  A separate 1024² raster union measures canvas occupied only by other LODs:
  24.45% before versus 5.57% after. These raster values include pixel-boundary
  dilation and are distinct from the quality evaluator's bbox fill.
- Cafe fill improves from 44.7% to 49.8%. Kitchen fill changes from 49.2% to
  48.6%; additional whole-panel matches displace intersecting sibling details
  into independent groups in exclusive mode. It is not claimed as a packing
  improvement on Kitchen. All eight exclusive LODs have zero invalid/collapsed
  faces and zero unexpected/intentional overlaps, with unchanged geometric
  triangles and healthy finer-island connectivity.
- Refusal reasons are retained per island (fit residual, whole-island quality,
  sibling contact or absent complete-face anchors); new-island refusals are no
  longer overwritten with the generic extension reason.

Remaining limitations: some families still have no finer correspondence,
including door/internal patches without complete-face anchors, and exclusive
mode keeps details that contact their parent surface in separate groups. Whole
finer rings and branches remain intact when no whole-island match exists;
coarsest cuts do not imply every finer chart is an I strip. Kitchen's larger
Fill candidates can still fail the precision/contact audit. Ordered overlap
baking is unchanged and unfinished. Zero defects do not establish complete
matching or optimal packing.


## Rejected forward seam subdivisions (2026-10-16)

Forward projection could keep generated vertices and triangles after rejecting
all projected pieces of an original face. Restore an original triangle only
when every piece retains its exact legacy UV and donor labels. Keep a whole
connected cut frontier whenever an accepted or changed neighbor still needs it;
nonmanifold edges refuse restoration. Original geometry, material slots and
vertex streams are copied through the existing refined-mesh path.

The restoration rechecks each changed face against the original winding,
absolute stretch and relative metric limits, then audits the complete output
against the original allowed contacts with the unchanged comparison budget.
Unchanged, already validated projected pieces retain their exact coordinates and
provenance. Cancellation cannot publish a partial result.

Private CPU NullDevice verification: 474 passed, zero failed, 16 optional skips,
including the new restoration regressions and both Transfer directions on a
vehicle at Adaptive / 10 tex/m. Its LOD1 count decreases from 12,733 to 11,566:
228 original triangles replace 1,395 rejected pieces, removing 1,167 generated
triangles without dropping any original geometric face. Exact captured output
confirms unchanged UV/donor labels on restored faces and exact barycentrics,
UVs and donors on every retained piece. Final Reverse has zero collapsed faces
and unexpected contacts. All final packing metric flags fit the existing
precision policies with one shared similarity per complete cross-LOD family;
maximum residual is below 0.0001 texel. No precision or quality limits change.

A wall control retains all 13 stable numeric outputs exactly. A material-correct
2:1 sofa control completes both directions without new quality defects; its
LOD counts change from 2,156/1,418/607 to 2,156/1,388/597. The separate sofa seat
seam, wall layout fragmentation/alignment and synchronous startup imports remain
open. This experiment does not claim to fix those problems or optimal packing.
Both FBX compilation variants pass; the new helper has no SonarAnalyzer 10.35
findings with the existing complexity and loop rules enabled. The measured
coverage selection includes the new regression suite and retains all existing
required suites, including viewport checks.


## Preserve selected Forward reference when preparing the Reverse foundation

Ordinary Transfer can rescue a formerly collapsed UV face with a projected donor while its healthy neighbors retain their prior coordinates. Treating the resulting UV islands independently can preserve a newly generated split on the coarsest Reverse foundation.

The selected Forward result now carries managed, nonserialized per-corner legacy UV and donor provenance alongside its exact ordered output geometry and UVs. Cleanup remaps retained faces. Missing or stale references retain the existing behavior; no static mesh cache or asset data is introduced.

Before foundation chart assignment and independent density normalization, only reference-connected components containing such a rescued face are considered. Original UV and donor discontinuities, mixed reference winding, nonmanifold or point-branch topology, ambiguous shared vertices and changes to outside incident corners are refused. Whole-component parameterization must retain reference winding and pass every current per-face absolute and relative metric, contact and work guard. Final cross-LOD packing validation remains unchanged.

Manual atlas resolution derives the existing prepared foundation density before reference repair. Outside incident aliases are checked by exact geometric position and current UV as well as mesh vertex identity: duplicating a UV-continuous point cannot hide an outside constraint. A pre-existing authored UV seam remains an independent side and is left untouched.

Portable regression cases cover generated splits, authored seams and winding, stale or missing provenance, cleanup remapping, nonmanifold input, outside incident corners, cancellation, and the actual Forward handoff after rejected subdivisions are restored. Private full-pipeline controls checked three actual models in both directions without publishing their assets. The targeted coarsest seam closed with exact endpoint UV agreement and unchanged output triangle geometry; the other two models retained all stable numeric outputs. This experiment does not resolve other layout or startup issues.

The selected reference also follows the existing final clamp-to-unit publication
policy. Its legacy coordinates, donor identities and rescued-face flags remain
unchanged; only the captured current coordinates receive the same clamp. Exact
prepared-mesh validation refuses stale provenance before this update. An actual
workflow publication and detached Reverse-input regression covers clamped,
unclamped and stale inputs without mutating the prepared mesh or selected record.


## Attach complete generated Reverse shells at compatible full edges (2026-10-17)

Native preparation of an invalid ordinary-Transfer island can split a large
surface into many disconnected charts. After that existing native fallback,
attach only complete generated charts. Prefer the longest compatible geometric
edge, keep the larger main chart immutable, move the whole donor by one
orientation-preserving similarity, then weld only donor-side endpoint aliases.
Equal face counts use metric area and stable face identity. No vertex averaging,
reflection, face removal or independent finer-LOD deformation is introduced.

A per-operation snapshot checks the complete original surface, not just the
current patch: exact source UV and available selected donor identities preserve
authored boundaries; outside position/UV aliases remain anchored, including
duplicated vertex indices. The full geometric edge must have exactly two uses.
Missing/stale patch context retains the existing native result. This managed
snapshot owns no Unity objects, static cache or serialized state.

An unanchored independent native chart blocking the donor may move only by one
translation. Contacts with the immutable main always refuse the attachment.
Each proposal and the complete patch retain winding, the original density-scale
bounds, absolute metrics and relative metrics measured against the original
native result, so successive joins cannot accumulate extra stretch. Complete
contact scans share the unchanged comparison budget; exhaustion publishes no
partial attachment. Final packing still validates complete cross-LOD families.

Verification includes actual ordinary and Reverse runs on three real inputs,
525 passed, zero failed, 16 optional skips; ordinary numeric outputs remain
exact and all 37,080 Reverse triangles retain exact Float32 geometry. A wall
LOD1 has 17 islands instead of 24, with 7 small islands instead of 12. All seven
LOD0 primary shells are axis-aligned and its atlas is 446 pixels at the same
80 texels/metre, below the fragmented baseline's 459 pixels (and the attachment
implementation's previous 473). All final atlases have zero collapsed/invalid
faces or unexpected contacts; the targeted seat seam stays closed. Rounding
flags fit existing precision policies using one transform for every corner of
each complete cross-LOD family. No quality or work limit is relaxed.

The frozen exclusive final pack now also considers conservative triangle
footprints at the already selected density. Choose the largest prepared island
on the finest LOD to orient each complete family, then move all descendants
together. Try permitted first-family quarter-turns because locally equal square
extents can leave different usable regions. Every order shares the existing
search/validation budgets; the candidate canvas can only shrink. Keep the
existing audited native result on refusal. Two procedural L-plus-rectangles
controls fail in the old search and pass with the bounded alternatives, for
both managed and native occupancy search. Private model references and passive
diagnostics are not part of this implementation.

Necessary seams remain when compatible full-edge poses intersect the immutable
main or fail the existing normal gate. Startup responsiveness remains separate.


### Remaining middle-LOD continuity defect (2026-10-17 follow-up)

The alignment and atlas measurements above qualify only those parts of the
change. They do not establish complete chain continuity. In the actual middle
LOD, a 292-face geometric/reference-edge region remains split into 253, 37 and
2-face UV pieces; a 196-face region remains split into 186, 8 and 2-face pieces.
Common packing-family labels retain their placement relationship but do not
make their UV edge topology continuous. These remaining cuts have not been
proved necessary under a jointly rebuilt chain layout. Full visual/correctness
acceptance is therefore still open.

Private whole-piece pre-matching and selected-legacy seam-provenance trials
passed their model checks but did not close this defect and are excluded from
this implementation. Direct finest-LOD UV replacement was rejected for large
comparative stretch regressions. No quality, winding, provenance or work check
was relaxed. A separate saved-data continuity acceptance check reproduces both
broken regions and must pass before the complete task is called finished.

## Recover exact selected-shell vertices before Reverse freezing

Contact-invalid ordinary interpolation can force Reverse to re-unwrap a whole
otherwise usable island. Before classification, try exact geometry vertices in
the actually selected source shell. Refuse ambiguous source UVs and incomplete
alias groups. Restore complete original alias groups on any original per-face
metric or winding refusal. Publish only after a complete zero-contact scan;
before and after scans share the existing bounded comparison budget. Healthy
inputs stay unchanged. UV0 remains correspondence evidence, not replacement UV2.
No new geometry, indices, cuts, metric allowances or native ownership are added.

The private three-model run passed 537 tests with 16 optional skips, in both
directions. The target middle LOD retains seven ordinary islands through Reverse
instead of the previous seventeen; the final atlas shrinks at the same density.
Whole-shell preservation is measured against prepared ordinary input. It does
not assert metric equality with the previously substituted native unwrap. An
existing coarse ordinary stretching defect remains outside this correction.
This section records the earlier local qualification; the ready correction is included in the combined PR.

Review follow-up: compare cumulative density against every original native face, not only the latest join; preserve an unchanged stretched face while recovering unrelated valid contacts. Changed faces retain all metric/winding checks and the final full-mesh audit. Extract the native stage without changing its lease or audit ordering. Two old cumulative-scale controls and one independent-recovery control fail as expected; the current full three-model and clean public runs pass with identical stable numeric outputs.

## LOD0 boundary bevel recovery (2026-10-20)

An optional furniture fixture reproduced collapsed bevel UVs in source preparation.
The frame's authored UV0 has 39 collapsed faces. The existing ARAP stage repairs
most, but its candidates for three remaining charts introduce contacts and are
correctly refused. Changing the ARAP initialization did not repair those charts
and was discarded.

After an ARAP refusal, develop only effectively collapsed boundary triangles
(nonfinite anisotropy or anisotropy above 10000) from an unchanged full edge.
Each placement uses the triangle's intrinsic 3D metric and the opposite side of
its already placed neighbour. Moderate existing stretch remains fixed; attempting
to unfold every face above 4 blocked this repair at vertices shared with the
healthy chart. Newly placed faces must themselves meet the existing limit of 4,
retain any nonzero original winding, and have exactly one neighbour across the
anchor edge. A queue propagates placements through the strip.

Publish only a complete candidate after the existing chart quality/contact audit.
An incomplete strip, ambiguous/nonmanifold anchor, new contact, or cancellation
publishes nothing. No geometry, UV0, topology, native code, or quality policy is
changed. On the supplied shelving frame, all three symmetry modes now produce
zero collapsed UV faces and zero overlap pairs, with exact oriented geometry
triangles retained. The shelf's existing 33 overlap pairs remain; this experiment
does not establish that every authored furniture chart is contact-free.

Related FBX diagnosis: a polygon's aggregate normal can disagree with an actual
imported triangle in a folded polygon. Use exact tagged-import triangle winding
when available, retaining the normal fallback for other topology. This removes
unresolved corners that previously prevented the existing local seam split.
Conflicting values still require a split; ambiguity is not silently accepted.
Optional copy-only round trips cover shelving and kitchen furniture, including
prepared LOD0 data. Geometry-degenerate triangles are reported separately from
UV-degenerate surfaces. Procedural controls cover mirrored/partially stretched
boundaries, refusal without partial publication, winding and seam conflicts,
and preservation of distinct thin wire edges.

Local Unity 6000.2.6f2 / Direct3D 11 verification on the main-based PR: 284 passed, zero failed,
7 optional fixture skips across FBX, transfer quality, source/group/hierarchical
repack, and viewport/rendering suites. Both FBX compile variants, identifier and
tool-dependency guards, and metadata coverage pass. Set
`MESHLAB_FBX_SEAM_FIXTURE` to a private FBX to run the copy-only source preparation
and export cases; proprietary assets are not committed. Round trips compare
exact cyclic triangle position/UV1 records and stored FBX position/UV0 values.
Artificial per-triangle seam stress applies to meshes without zero-area geometry;
degenerate meshes still exercise ordinary and prepared channel writes.
The kitchen's Main retains four original geometry-degenerate faces; its
Countertop still has contact/stretch defects. Successful channel export
does not certify an otherwise perfect lightmap layout.

Review follow-up: reject nonfinite original UV areas before the winding-sign
comparison, so invalid input cannot throw or publish a partial recovery. Both
NaN and infinity refusal controls pass, alongside the kitchen round trips and
source preparation (12/12 focused tests).

CodeRabbit follow-up: a welded multi-quad strip can reach a closing face whose
three vertices were already placed by neighbours. Validate that existing pose
through the same manifold-anchor, metric, finite-area and winding checks instead
of leaving it pending. Two traversal-order controls reproduce the refusal before
the fix; both now pass. A distorted closure still refuses the complete candidate.
All 15 focused shelving/export/recovery tests pass after this correction.
