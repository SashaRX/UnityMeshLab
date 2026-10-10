# Generated annular Bridge and virtual averaged co-normal growth

Current production (revision 15): automatic collar ambiguity also respects the
explicit **Cap on Bridge refusal** option. Without it, an ambiguous partner graph
still refuses Bridge. With it, no partner is guessed: each selected ambiguous rim
tries its own planar disk under the existing topology, contact and volume audits.
The preview/capture retains the ambiguity reason and separate fallback masks.

The 128-edge Bridge guard is replaced by a
pre-allocation complete-profile budget (600,000 states and a conservative 64 MiB
retained-search estimate). General closure preparation still bounds individual
contours to 512 edges. Historical results below retain their original limits.

### CafeChair_12 captured refusal (2026-10-10)

The saved automatic preparation selected all ten loops, used plane tolerance
0.01 and enabled Cap fallback. Eight loops had multiple collar candidates; the
old ambiguity check refused them before entering the Bridge-to-Cap retry path.
Only two ordinary Caps succeeded, leaving 44 open edges.

Revision 15 closes all ten loops with 36 added triangles and zero remaining open
edges on the exact captured donor geometry. No Bridge is inferred, source corner
positions stay unchanged, and every closed component passes the volume audit.
Public controls exercise the same ambiguity at scales 0.001, 1 and 1000 with
reversed winding, both strict and partial preparation, and fallback disabled.

`RemeshCapComparisonTests.VerifyCapturedClosureWithFallbackThroughNativeStages`
replays an opt-in manifest with `cases[].source`, `cases[].settingsJson` and
`cases[].sourceFaceOwners`. It requires successful closure and then runs native
Remesh, source-fitted Simplify and UV using those saved settings. The original
donors must remain unchanged and the final atlas scan must be complete and clean.
Outputs include the closed support mesh and `closure-native.json`.

The complete saved-setting replay with voxel revision 1 reaches 241,028 voxel faces, 16,938 simplified
faces and 754 UV islands (mean/worst stretch 1.03814/8.461). Its full atlas scan
has no overlap, degenerate, invalid or out-of-bounds faces/vertices. Worst stretch
remains a quality defect; material Bake was not evaluated. Saved brute-force
packing took about 14 minutes, so this opt-in test has a 30-minute timeout rather
than the runner's default three minutes. The initial run completed every geometry
assertion but was reported failed by that default timeout.

The fresh voxel-2/surface-fit-2 saved-setting case passes in 1,557.9 seconds:
the same 10 closed rims / 36 added faces lead to 241,028 voxel faces, 30,128
simplified faces and 663 islands (397 small), with mean/worst stretch
1.07793/7.36968. The complete final scan has zero overlaps, degenerate or
invalid UV faces and out-of-bounds vertices. Donors remain unchanged. This
supersedes revision-1 downstream numbers, but does not certify texture Bake
or acceptable interactive performance. See the
[surface-fit replay](REMESH_SURFACE_FIT_REPRO.md#roundoff-face-normals-after-corrected-voxel-recovery)
for the intermediate packed-candidate refusal and final broad result.

2026-10-09, following the [paired-rim topology counterexample](REMESH_CAP_BRIDGE.md).
The offline `bridge.py` now generates candidate strips from two explicitly
selected boundary loops, without supplied reference triangles. `collar_growth.py`
measures virtual boundary expansion as supporting evidence. Neither implements
automatic disk/Bridge classification or Unity integration.

## Virtual expansion evidence

For each boundary vertex, average the two adjacent boundary-face normals with
adjacent edge-length weights. Average the unit boundary tangents, then normalize
`cross(tangent, averageNormal)` to obtain an outward-from-source co-normal.
This continues the surface across its open border. It is different from moving
along the ordinary face normal, which offsets the surface. Unstable/cancelling
directions require refusal rather than an invented direction.

Evaluate both signs using bounded straight vertex trajectories. Coordinates are
centered and normalized by the selected rims' combined bounding-box diagonal.
Default reach is one such diagonal, hit tolerance `1e-4`, and minimum advance
`1e-5`. Reach cannot exceed 16 diagonals in this local profile. Closest approaches
include skew and collinear-facing segments; both trajectories must advance.
Immediate self-neighbor pairs are excluded.

Report self-convergence coverage and spread, between-loop near-hit counts,
mutual nearest partners, coverage of each rim and meeting distances. Default
limits: 64 vertices per loop, 20,000 pair tests, 8,192 source faces and 16,384
positions. Cancellation/budget refusal clears incomplete side evidence.
No source vertex is moved. These are approximate trajectory meetings, not
exact polygon intersections or a collision certificate for the swept band.

| Fixture | Outward-from-source evidence | Opposite-sign evidence |
| --- | --- | --- |
| Cut torus, 8 vertices per rim | 8 mutual pairs, 100% of both rims | No between-rim meetings |
| Box missing opposite faces | No between-rim meetings | 4 mutual pairs, 100% of both rims |
| Inner rim of a flat annulus | Entire rim converges near its center | No self-convergence |

This supports the proposed distinction: a contour can converge toward its own
hole or continue toward another rim. The box shows why unsigned hit counts
alone mislead. Even signed evidence is not a universal classifier: curved
continuation, corners, noise and unequal tessellation can miss matches, and
trajectories may travel through occupied geometry. Keep distance, mutuality,
coverage, source collar and topology/shape alternatives; audit actual candidates.

## Bounded strip construction

Caller chooses **annular Bridge** and provides two snapshot-bound loop IDs.
Fresh extraction checks simple disjoint rims and globally non-singular manifold
source connectivity. No automatic rim pairing or expected-genus inference occurs.

Keep the first rim in source order; traverse the second in reverse source order.
Enumerate its cyclic seam offsets. A dynamic program advances one rim edge at
a time, creating one triangle per advance. The candidate has `m+n` triangles,
so unequal edge counts need no border resampling, edge splitting or new vertices.
Each selected source edge is opposed by one new face.

The current seam profile starts with an A-edge advance and ends with a B-edge
advance, disallowing early full-ring runs that repeat a seam cross-link. Retain
the two cheapest paths per phase by default, with a maximum of eight. This is
a restricted monotone zipper family, not exhaustive annulus triangulation.
If its candidates all fail, report that this family failed, not that no Bridge
can exist. Broader seam profiles/interior vertices remain future experiments.

Normalize geometry to the selected rims' diagonal. Reject triangles below the
explicit `1e-12` normalized doubled-area threshold. The score sums
`sum(edgeLengthSquared) / doubledArea + 2*(1 - normalDotCollar)` per triangle.
The collar normal is the adjacent source face normal. This combines triangle
shape and surface continuation; it is not a confidence or acceptance threshold.
Virtual averaged co-normal evidence is recorded separately, without forcing a
closure type or adding a hidden hard match constraint.

## Independent gates and selection

- Preserve original position/index arrays exactly as the protected prefix.
- Remove exactly the two selected rim occurrence sets, preserving every
  unselected boundary. New bridge faces may leave no additional boundary.
- Re-extract topology: no duplicates, degenerates, bad winding, non-manifold
  edges or disconnected fans.
- Audit patch topology independently: one connected annulus, two boundary
  loops, Euler characteristic zero and genus zero.
- Audit assembled topology: Euler characteristic remains unchanged; two rims
  disappear. Same-component Bridge retains component count and can add a handle;
  between-component Bridge reduces component count by one.
- Exactly audit new/old and new/new pairs, including improper adjacent overlaps.
  Unchanged old/old geometry is not re-certified by this local gate.
- Recompute remaining plane hypotheses for each passing candidate.

Only after the complete bounded search choose the lowest-score audited strip,
retaining diagnostics for alternatives. Cancellation or a budget exceeded after
a provisional winner still returns unchanged source arrays. Limits are 64 edges
per rim, 64 phases, 100,000 DP grid states, 128 unique candidates, 200,000 exact
pair tests shared across candidates, plus the existing mesh/P2 limits.
`accepted=true` explicitly retains `intendedShapeCertified=false` and
`solidCertified=false`; geometric/local topology acceptance cannot prove intent.

## Measurements and reproduction

| Case | Result | Audited alternatives | Added triangles | DP states / exact pair tests |
| --- | --- | ---: | ---: | --- |
| Cut torus, 8+8 rim edges | Generated Bridge accepted, closed genus 1 | 16 | 16 | 648 / 3,796 |
| Cut torus, 6+7 rim edges | Generated Bridge accepted, closed genus 1 | 14 | 13 | 392 / 2,862 |
| Box requiring separate disks | All profile candidates refused | 0 | 0 published | 100 / 0 (topology refusal) |
| Torus gap blocked by a separate wall | All profile candidates refused | 0 | 0 published | 294 / 2,615 |

The unequal-rim fixture subdivides an existing source boundary triangle before
generation. Generator inputs remain unchanged. Independent whole-mesh exact
intersection and topology audits pass on both generated torus results. These
triangulations need not equal the original torus strip; shape selection remains
a separate policy and evidence requirement.

```powershell
python 'Tools~/RemeshCapBenchmark/bridge_generation_probe.py' --output PRIVATE_GENERATION_REPORT.json
python 'Tools~/RemeshCapBenchmark/bridge.py' --source SOURCE.bin --source-hash SOURCE_HASH --loop LOOP_A --loop LOOP_B --output PRIVATE_BRIDGE_REPORT.json --mesh-output FRESH_CANDIDATE.npz
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
```

Obtain snapshot hash and loop IDs from `boundaries.py`. Bridge Options fields
are CLI flags with underscores replaced by hyphens. Accepted CLI runs write
strict JSON and optionally a fresh NPZ with `positions`/`indices`; existing mesh
output paths are refused. Exit 2 writes diagnostic refusal and no new mesh.
CLI acceptance does not authorize applying a candidate to a Unity asset.
Private report: `_results~/cap-structure-20261009/generated-bridges.json`.

**105/105 offline tests pass** with NumPy 2.1.3 and Triangle 20250106 for existing
compound tests. Thirteen new generator methods cover unequal rims, repeated
determinism, winding/order, transforms, source seams, unselected rims, component
joining, blocked gaps, bad coverage, budgets and cancellation. Six growth tests
cover torus/box/flat-hole evidence, signs, skew/collinear trajectories and refusal.
The reference probe and accepted/refused CLI paths pass; exported NPZ source
prefix, genus and independent intersections were verified.

No original UV/material/normal/tangent streams are captured or assigned to new
faces by these position/index tools. Managed attribute policy, bake donors,
Undo/lifecycle and native Remesh effects still require Unity integration and
actual Editor tests. Production code, plugins and package version are unchanged.

## Production completion, 2026-10-10

The preceding sections record the original offline experiment. The managed
production zipper is now integrated into closure preparation (Cap revision 12).
It preserves both source rims, connects unequal vertex counts without resampling,
and leaves every unselected boundary unchanged. Explicit Bridge selects exactly
two loop numbers; Automatic still requires a unique mutual collar continuation.
Multiple independent automatic pairs are prepared separately alongside ordinary Caps.

The previous production profile could exceed the contact budget even on a valid
64-edge torus band. The new profile completes all dynamic-programming states
before auditing candidates sorted by score. It returns the first candidate
passing exact topology and contact gates, without auditing higher-score
candidates after an accepted winner. Budget exhaustion before acceptance and
cancellation still publish no partial patch.

Production limits differ from the original offline generator: 128 edges per rim,
at most 32 seam phases, eight retained paths per grid state, 600,000 grid states,
and the existing shared 2,000,000 contact-pair budget. For rims above 32 edges,
16 phases cover the circumference and 16 concentrate on short cross-rim links.
The smaller rim supplies the phase profile. Search is bounded, not exhaustive;
its refusal does not prove that no possible Bridge exists.

Every candidate preserves the protected face prefix and exact unselected
boundary set. Its patch must be one manifold annulus with precisely the selected
boundary edges and Euler characteristic zero. Assembled Euler characteristic
remains unchanged. Same-element closure retains component count; joining
two elements reduces it by one. Both joined elements and earlier synthetic faces
are protected in the contact audit. Unrelated elements do not block preparation.
The preview names both connected loops, reports the completed search profile,
and shows the first rejected face contact when no candidate passes. Each accepted
Bridge remains one synthetic patch for mask projection and later region removal.

Local Unity closure regressions include 64+64, 128+128, 6+7, 64+65 and 96+97 rims,
reversed winding, rigid transforms/scales, an untouched third opening, separate
element joining, blocked gaps, atomic budgets and several independent automatic
pairs. Six native torus runs (32/64/128, source fitting off/on) retain genus one
through Voxelize, Simplify and Unwrap, with complete UV audits and zero overlaps,
degenerate faces or out-of-bounds UVs. These checks do not certify every real asset.

### Independent upstream comparison

`compare_bridge.py` prepares a private manifest and compares Unity production
exports against actual MeshLib `3.1.4.297` stitching with complex and universal
metrics. MeshLib documents stitching as a cylindrical patch between two
holes: [stitching example](https://meshlib.io/documentation/ExampleMeshStitchHole.html),
[API and metrics](https://meshlib.io/documentation/Cpp/group__FillHoleGroup.html).
This is an isolated benchmark dependency, not a Unity runtime dependency.

All three methods pass independent preservation, boundary, annulus, Euler,
component and exact-contact checks on five known torus fixtures. Three real
capture pairs are exploratory: their authorial Bridge intent is unverified.

| Captured pair | Production | MeshLib complex | MeshLib universal |
| --- | --- | --- | --- |
| Univer_Column_C, loops 0/1 (22+24 edges) | Accepted, 46 faces, zero improper contacts | 4 improper contacts | 5 improper contacts |
| Park_Bench_A, loops 3/4 (52+52 edges) | Refused after 256 audited candidates | 472 improper contacts | 651 improper contacts |
| Garbage_Chute, loops 0/2 (24+24 edges) | Refused after 192 audited candidates | 93 improper contacts | 410 improper contacts |

Acceptance is local geometric evidence. The accepted column probe still has
other openings and is not a closed remesh input. Native defects previously
observed on the bench are not repaired by these Bridge changes.

```powershell
python 'Tools~/RemeshCapBenchmark/compare_bridge.py' --output PRIVATE_OUTPUT
# Set MESH_LAB_CAP_COMPARISON_MANIFEST=PRIVATE_OUTPUT/manifest.json and run
# RemeshCapComparisonTests.GenerateBridgeCandidates in the Unity EditMode runner.
python 'Tools~/RemeshCapBenchmark/compare_bridge.py' --output PRIVATE_OUTPUT --audit
```

Append `--capture 'NAME|SOURCE.bin|LOOP_A|LOOP_B'` during preparation for private
captures. Reports and geometry are stored only under the chosen private output.
This run: `_results~/bridge-20261010/comparison/comparison.json` (24 outcomes:
16 locally accepted, 6 library outputs rejected by audit, 2 production refusals).
The full offline geometry suite passes 128 tests; six cover the independent
Bridge comparison gates, including rejection of separate disks on a torus.

Two-cut controls on one torus also pass in Unity: 16 longitudinal samples and
eight vertices per rim, with the second missing band either opposite (band 8)
or nearby (band 3). Automatic closes four rims with two independent Bridges,
each across its own missing band. After the first Bridge there is one connected
component with two open rims; after the second there is one closed genus-one
component. Original vertices/faces are preserved and prepared element labels
are unified. This is evidence for these two controls, not a guarantee for
arbitrary ambiguous pairs or intersecting contours.

## Optional planar Caps after Bridge refusal (revision 13)

`Cap on Bridge refusal` is available in Bridge and Automatic. It is disabled in
new and older saved settings. After an actual paired Bridge attempt refuses,
preparation rolls back that candidate and tries a planar disk on each original
rim independently. Successful Bridges remain Bridges. Invalid selections and
cancellation do not trigger a substitute closure. Since revision 15, ambiguous
Automatic pairings use the same explicit fallback without selecting a partner.
Contact exhaustion does not reset the shared audit budget.

Each disk retains the current element contact scope, source vertices/faces,
topology checks and its own synthetic patch ID. A refused disk leaves that rim
open and does not discard a valid disk on the other rim in partial preparation.
Nonplanar rims still refuse the planar disk; this fallback does not introduce
Surface Caps or invent a compound closure.

Separate disks change the intended shape. On one torus with two missing bands,
four fallback disks leave two closed tube segments (Euler 2 each), whereas two
successful Bridges restore one genus-one component (Euler 0). The preview labels
accepted fallback rims `CAP FALLBACK`, reports the original Bridge refusal and
keeps refused Caps red. Diagnostic metadata distinguishes fallback attempts from
rims that actually remain refused, and includes the saved opt-in setting.

Unity controls cover 129-edge rim refusals, nearby/opposite double cuts, a blocked
Bridge whose disks are valid, one blocked disk without losing its partner, a
retained successful Bridge, invalid selection, cancellation, settings/cache
invalidation and the shared contact budget. A native control carries the capped
tube through voxel Remesh, Simplify and UV, checking genus and atlas overlaps.

Validation on 2026-10-10: 154 Unity EditMode controls passed, zero failed;
29 optional private-capture cases were ignored because their inputs were not
configured. Six diagnostic capture controls also passed after adding the fallback
metadata. Both FBX compile variants have zero errors. Private results:
`_results~/bridge-cap-fallback-20261010/closure-final.xml` and `metadata.xml`.

## Circumferential slit on one connected torus

A longitudinal strip removes one minor-angle band all the way around the major
circle. The remaining surface is one connected annulus with two circumferential
rims and Euler 0. This is distinct from transverse cuts producing separate tube
segments. Bridge must add one annulus between the two rims and leave a single
closed genus-one component. Separate disks can pass contact/topology checks on
the inner slit but close the central handle instead (Euler 2).

Production controls cover outer/top/inner/bottom strips and the minor-angle wrap,
16/24/32/64 edges per rim, transformed/scaled reversed winding, exact donor/patch
preservation, and circumferential edge locality (no chords across the hole).
Automatic selects Bridge and retains it even when Cap fallback is enabled.
With 129-edge circumferential rims the existing Bridge limit refuses; the opted-in
planar fallback closes an inner slit into one genus-zero component, not two
segments. The test records that shape change explicitly.

On 2026-10-10 all 66 Unity closure controls passed with no skips or failures.
The 14 new cases include four resolution-64 native Remesh/Simplify/UV runs on
outer/top/inner slits with source fitting off/on. Every native stage retains one
closed genus-one component; complete atlas scans report zero overlaps,
degenerates and out-of-bounds vertices. Private results:
`_results~/bridge-longitudinal-20261010/longitudinal.xml` and `closure-all.xml`.
The offline suite passes 130 tests, and the default comparison manifest now
includes all three circumferential slit fixtures. No production algorithm change
was required for these controls.

The opt-in comparison exporter preserved every source corner and wrote actual
production Bridge, Automatic and Cap meshes for the outer/top/inner fixtures.
Independent exact-contact and annulus-intent audits accept all nine Bridge
outputs (production, MeshLib complex and MeshLib universal on each of the three
fixtures). The inner pair of production planar Caps adds 44 faces, passes the
local closure controls and has genus 0, so it fails the Bridge intent gate.
Private evidence: `comparison/comparison.json`, `comparison/caps-intent.json`
and `comparison/longitudinal-preview.png` under the longitudinal result folder.

## Two-to-one rim segmentation

The rim edge counts do not need to match. The 128-edge limit applies to each rim
independently and bounds search allocations/work; it is not an annulus topology
constraint. The search also has separate state, phase, retained-path and exact
contact budgets. Removing only the edge guard would not make larger searches
unboundedly supported.

An 8-versus-16 control splits every original edge on one rim at its midpoint,
retriangulating adjacent donor faces without changing the source surface. All
four Unity controls pass: explicit/Automatic Bridge and transformed, scaled,
reversed winding. They add exactly 24 triangles in one annulus, preserve every
donor vertex, oriented face and boundary edge, and retain one closed genus-one
component. The enabled Cap fallback is never used. This verifies a two-to-one
segmentation difference on matching contours, not every possible rim shape.

The fixture is included in the default autonomous comparison manifest as
`Unequal8x16`. Private Unity evidence:
`_results~/bridge-eight-sixteen-20261010/eight-sixteen.xml`. The complete closure
suite passes 70 tests with no failures or skips (`closure-all.xml`); the offline
suite passes 130 tests. Both reference-assembly FBX define variants compile
without errors. No production algorithm change was required.
The opt-in Unity exporter and independent exact-contact/annulus audits also
accept the production output and both MeshLib stitching variants on this same
fixture (three accepted results in `comparison/comparison.json`).

## Pre-allocation budget and real-pair diagnosis (revision 14)

The complete phase profile is checked before topology/search storage allocation.
The state estimate is `(m+1)*(n+1)*min(m,n,32)`, evaluated with saturating 64-bit
arithmetic. The retained-storage estimate covers grid cells/lists/backing arrays,
eight path nodes per cell and terminal ancestry across every phase. It has a
64 MiB budget; this estimates search storage, not total process heap or transient
GC allocations. The 600,000-state runtime guard and shared exact-contact budget
remain. No partially searched winner is published. Revision 14 invalidates old
prepared results. The outer closure pipeline retains its separate 512-edge
contour limit.

Unity accepts 8/32, 8/256 and 64/256 rims, including uneven subdivision and a
shifted rim. The former 129/129 refusal now succeeds. The fallback controls use
138/138, which exceed the complete-profile budget and still independently audit
both opted-in planar disks. Separate tests cover memory refusal, integer-limit
overflow saturation, cancellation, and immutable donors/contact diagnostics.
All 82 closure controls pass. The combined closure/planar run has 143 passes,
zero failures and 16 optional private-input skips. Both final FBX define builds
and the 130-test offline suite pass.

Two resolution-64, 8/256 controls with Solve off/on retain one closed genus-one
component through Remesh, Simplify and Unwrap, with complete UV scans and zero
overlaps/degenerates/out-of-bounds vertices. Projected masks retain original and
Bridge faces. CPU Bake counts 824/1857 and 903/1941 missed/covered Bridge texels:
original donors lack the synthetic strip, so this verifies mask/miss accounting,
not complete texture coverage of invented geometry.

The default comparison manifest now contains 12 controls. All 36 production and
MeshLib complex/universal results pass independent annulus, preservation and
exact-contact audits. The two captured explicit pairs remain refused. Production
mutual-collar selection rejects Park_Bench_A loops 3/4 and Garbage_Chute loops
0/2 as automatic Bridge pairs. Their first rejected strips intersect existing
geometry; the bench strip spans its back, and the chute strip follows the existing
tube wall. Independent audits find 1952 and 93 improper contacts respectively.
First witnesses are new/donor faces 2344/1248 and 498/321. Opt-in exports and
`plot_bridge_rejections.py` retain diagnostic geometry/images without applying it.

Production planar/local Caps at .01 and Automatic close all rims on both captures:
128 added faces on Park_Bench_A and 66 on Garbage_Chute. Increasing the chute's
plane tolerance to .1 instead leaves one rim (10 edges), so a looser tolerance is
not uniformly better. Native replay of Automatic supports at resolution 64 with
Solve off/on passes Remesh/Trim/Simplify/UV for Garbage_Chute (68 simplified
faces, zero UV overlaps/degenerates). Park_Bench_A fails before Trim in voxel
remesh in both modes: 176 duplicate faces, 218 non-manifold edges and 204
disconnected vertex fans. These are outcome rows, not successful pipeline runs;
the test exporter itself passing does not certify those failed rows.

Private evidence is under `_results~/bridge-budget-20261010/`: `closure-final.xml`,
`export-final.xml`, `real-caps.xml`, `native-real.xml`, `comparison/production.json`,
`comparison/native.json`, `comparison/comparison.json` and `rejections/`.
