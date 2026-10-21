# Furniture transfer and packing audit — 2026-10-21

The first broad furniture audit inventories 80 private FBX files. 62 contain
matched LOD0-to-lower-detail pairs: 247 distinct source submeshes and 255 target
pairs. The other 18 files have no matched pair under the naming convention and
are explicitly excluded, not counted as passing transfer cases. Nine rows per
pair produce 2,295 measurements. Every intersection scan completes; no processing
error occurs. Original FBX bytes remain unchanged.

The production baseline is `e7a74aab` after the point-junction repair in PR #290.
The harness uses Unity 6000.2.6f2, 512 resolution, square texture metric, symmetry
off, UV0 welding and triangle pre-optimization. This measures ordinary per-pair
grouped transfer, not all UI Full Pipeline recovery stages, shared multi-part
atlases, material texture-aspect detection or Reverse transfer. Reproduction and
metric definitions: [corpus tools](../Tools~/FurnitureCorpus/README.md).

## Baseline defects

Among 247 unique prepared source submeshes, 23 have UV overlaps and 16 have
degenerate faces. Among 255 ordinary transfer results, 69 have overlaps and 49
have degenerate faces under the original Jacobian-only metric; the corrected
stored-area check finds 53. None have nonfinite/invalid faces. These categories overlap
and must not be added to infer the number of defective meshes.

| Target | Overlap pairs | Exact 3D zero-area faces | Zero-area UV faces |
|---|---:|---:|---:|
| PunchingBag_A_LOD1 | 1605 | 0 | 64 |
| Modern_Bed_A_Blanket_LOD2 | 780 | 2 | 3083 |
| Modern_Bed_A_Blanket_LOD1 | 436 | 0 | 1582 |
| Modern_OttomanBench_A_Frame_LOD1 | 350 | 0 | 11 |
| Modern_Shelf_A_Corner_LOD1 | 234 | 0 | 43 |
| Modern_Chair_B_Frame_LOD1 | 211 | 0 | 8 |
| Modern_ShelvingUnit_Shelf_LOD1 | 0 | 0 | 4 |

The shelving fixture's remaining four degeneracies are therefore collapsed UVs,
not zero-area geometry. Extreme anisotropy remains a separate issue even in
results with zero detected overlaps. The two original fixtures alone substantially
underrepresent failures in curved and coarse geometry.

The original audit also exposed a diagnostic discrepancy:
`Modern_Kitchen_A_Countertop_LOD1` has one exactly zero stored UV-area face in the
direct counter, while the original Jacobian-based `degenerateFaces` reports zero.
Its finite worst anisotropy is about 5.94e15. A finite singular-value ratio alone
must not establish that a face survived. The corpus comparison includes both
direct degeneracy counters as well as the existing quality metrics; the table's
outcomes were unchanged by that independent comparison guard. The production
correction and a repeat of the corpus are recorded below.

## Compared alternatives

Each row below compares 255 target pairs against default grouped transfer.
“Better” means no measured quality component gets worse within the report's
relative 1e-6 float tolerance and at least one improves; it does not establish
correct correspondence. “Tradeoff” means both improvement and regression occur.
Overlap pair area participates even for small contacts; no diagnostic is suppressed.

| Alternative | Better | Worse | Tradeoff | Same |
|---|---:|---:|---:|---:|
| UV0 nearest | 2 | 181 | 54 | 18 |
| Surface nearest | 0 | 193 | 61 | 1 |
| Surface nearest with normal filtering | 1 | 189 | 62 | 3 |
| Disable source reparameterization | 19 | 163 | 60 | 13 |
| Request internal oversampling 8× | 70 | 60 | 125 | 0 |

This table uses the corrected stored-area metric from the repeat below. Finite
stretch statistics exclude collapsed faces, whose defects remain explicit in
`degenerateFaces`; a lower worst stretch alone is not a repaired layout.

No alternative is a justified global replacement. Disabling reparameterization
never dominates the default source quality in these 255 pair-associated source
rows, even though some target results improve: source and target quality must be
evaluated together. Raising oversampling improves many source charts but also
changes placement and Float32 rounding, with mixed downstream results. It is not
accepted as a default merely because average source stretch may improve.

Source rows repeat for models with several lower LODs. The comparison table is
pair-weighted; it is not a count of independent FBX models. Timings are single
samples and do not support performance claims. Triangle area sums are not atlas
occupancy in overlapped layouts; padding and gutter quality are not certified.

## Stored-UV degeneracy correction

The quality report and Reverse UV triangle metric now check the determinant of
stored UV edges before geometry-frame conversion. The diagnostic checks finite
UVs and metric values first. No area epsilon is introduced. A synthetic oblique
triangle reproduced the failure in seven of twelve winding/corner-order cases;
all twelve now pass, along with two thin nonzero-area controls and four nonfinite
input controls. The checks cover both repeated UV corners and three distinct
collinear UV corners.

A repeat of all 80 files / 255 pairs / 2,295 rows completes without processing
errors or incomplete intersection scans. 136 rows correct their degeneracy and
finite-stretch statistics, including 30 default grouped target results. The
combined degeneracy count no longer falls below either independent geometry or
UV counter in any row. All other recorded fields except execution time match
the previous run exactly: overlaps, bounds, direct degeneracy counts, UV area
sums, atlas sizes, misses and input hashes. This is not a bytewise comparison of
output UV buffers, which the corpus does not record.

The countertop now reports its one collapsed UV face in every symmetry mode;
its worst remaining finite anisotropy is about 2.21e10 instead of 5.94e15. This
decrease comes from excluding the collapsed face, not from repairing its UVs.
The original 49 degeneracy-positive default targets become 53. The corrected
metric can refuse candidates that previously appeared nonsingular; this guard
does not implement a new repair algorithm or validate every UI workflow.

## Candidate-scoring experiment — rejected

Two controlled trials used the corrected diagnostic baseline above. The first
promoted grouped candidate geometry/frame and area-weighting arithmetic to double
precision and replaced the absolute `1e-10` UV-area issue threshold with a direct
zero-area test. The second retained only the area-threshold change. The existing
10000:1 effective-collapse threshold and overlap checks were retained in both.

Portable probes exposed two real scoring defects: a valid uniformly scaled
triangle at UV scale `1e-6` is counted as an issue, and an isometric oblique bevel
reports stretch about 1.526 rather than 1. Both corrected controls passed in the
full trial. However, this score also drives source ARAP selection and transfer
decisions; correcting it is not behavior-neutral.

Each trial completed 80 files / 255 pairs / 2,295 measurements without processing
errors or incomplete scans. Both yield the same default target classifications:
249 unchanged, 2 improvements and 4 tradeoffs. Representative full-trial results:

| Target | Before | Trial |
|---|---|---|
| Modern_Bed_A_Blanket_LOD2 | 3083 degenerate faces, 780 overlaps | 3041 degenerate faces, 932 overlaps |
| Modern_Dresser_B_Frame_LOD1 | 11 overlaps | 12 overlaps |
| Modern_OttomanBench_A_Frame_LOD2 | 33 degenerate faces, 81 overlaps | 34 degenerate faces, 77 overlaps |
| Modern_OttomanBench_A_Main_LOD2 | 31 overlaps | 51 overlaps |

The full trial passed 538 local Unity checks (35 optional fixture skips); the
area-only trial passed 100. Those passing tests do not override the measured
corpus regressions. Both changes are excluded from production. The original
small-area heuristic and grouped geometry metric remain known limitations.
Two source rows improve while downstream results include regressions, reinforcing
the need to qualify source preparation and every target LOD together.

The report tool now compares two complete captures and can fail explicitly on
regressions or tradeoffs. This reproduces the rejection of both trials instead of
relying on an overall average. The corrected stored-area diagnostics remain in
production; no new transfer/packing default is selected by this experiment.

## Target boundary recovery — accepted within the existing footprint

The next continuation applies intrinsic boundary development to ordinary target
UVs after topology correction. Healthy vertices stay fixed. Every changed face
must replace effective collapse with finite anisotropy at most four; a shared
vertex, rejected shell or mixed donor assignment refuses the candidate. The
complete changed triangles must fit the union of their selected donor's UV
footprints, including the existing quarter-texel boundary band measured using
the actual packed atlas size. Overlapping donor faces cannot count coverage twice.

Complete before/after target scans retain unrelated existing contacts. Changed
faces may not participate in an existing contact, because retaining the pair
alone would not prove its area stayed fixed. Any new contact, invalid or
degenerate face, out-of-bounds increase or incomplete scan refuses publication.
The original UV buffer remains intact until accepted repairs and cancellation
checks finish. Source preparation, geometry, indices and native packing stay
unchanged.

The full 80-file / 255-pair / 2,295-row repeat passes the baseline regression gate
with four better rows and 2,291 unchanged rows; no tradeoffs or worse rows occur.
All intersection scans complete and there are no processing errors.

| Target | Profile | Collapsed UV faces before → after | Overlap pairs before → after |
|---|---|---:|---:|
| Modern_DressingTable_B_Shelf_LOD1 | default | 7 → 6 | 4 → 4 |
| Modern_DressingTable_B_Shelf_LOD1 | no-reparameterization | 7 → 6 | 6 → 6 |
| Modern_DressingTable_B_Shelf_LOD1 | oversample-8 | 7 → 6 | 7 → 7 |
| Modern_Bed_A_Blanket_LOD2 | no-reparameterization | 3074 → 3073 | 808 → 808 |

At this immutable-anchor stage the countertop remained a documented refusal. Developing
its free corner removes the collapsed face geometrically, but moves it about
0.7 texel beyond the donor at its actual atlas side of 2344. Testing against the
requested 512 instead would incorrectly treat that displacement as only 0.153
texel. The production check retains the 0.25-texel limit and its original one
collapsed face. The shelving fixture's four collapsed UV faces also remain.
Moving a healthy neighbour required the separate qualification below.

## Incident-patch recovery — retaining whole-model stretch

The next stage permits one corner to move and audits all of its incident faces.
For each effectively collapsed triangle it tries intrinsic development against
each opposite edge, in both orientations. A healthy incident face must anchor
the patch; nonmanifold edges and changed nonzero winding refuse it. Healthy
incident faces retain the existing relative metric allowance, and every changed
face must have anisotropy at most four. Donor-union coverage, the actual atlas
size, quarter-texel band and complete contact audits remain enforced.

The initial trial improved 32 of 2,295 rows but introduced three tradeoffs:
weighted stretch increased on DressingTable_A_Frame_LOD1 in default and
no-reparameterization, and Kitchen_Island_A_Countertop_LOD1 in oversample-8.
The regression gate rejected this trial. The qualified version additionally
requires whole-model maximum and area-weighted finite stretch and stretched-face
counts to retain the original quality, at the report's relative 1e-6 precision.
That original reference is fixed across repairs, preventing cumulative drift.

For Modern_Kitchen_A_Countertop_LOD1, moving one shared corner recovers the
collapsed triangle while its four healthy incident faces remain near-isometric
(worst about 1.023). The original free-corner proposal is still outside the donor
and still refused. This patch fits within the donor band at atlas side 2344,
preserves existing nonzero winding and retains four unrelated overlap pairs.
The prepared LOD0 source, geometry, native packing and mesh indices are unchanged.

The guarded repeat completes all 80 files / 255 pairs / 2,295 rows, with zero
errors or incomplete intersection scans. Against the immutable-anchor stage:
eight rows improve and 2,287 stay equal under the comparison; zero rows worsen
or trade quality. All source and alternate projection rows retain their measured
quality. Both this comparison and the cumulative comparison against the corrected
diagnostic baseline pass `--require-no-regressions` (ten better rows cumulatively).

| Target | Profile | Collapsed UV faces before → after | Overlap pairs before → after |
|---|---|---:|---:|
| Modern_Kitchen_A_Countertop_LOD1 | default | 1 → 0 | 4 → 4 |
| Modern_Kitchen_A_Countertop_LOD1 | no-reparameterization | 1 → 0 | 2 → 2 |
| Modern_Kitchen_A_Countertop_LOD1 | oversample-8 | 1 → 0 | 5 → 5 |
| Modern_DressingTable_A_Frame_LOD1 | oversample-8 | 11 → 9 | 4 → 4 |
| Shelf_C_LOD1 | default | 9 → 8 | 72 → 72 |

The other three improvements reduce weighted stretch without changing collapse
counts: Blanket_LOD2/default and DressingTable_B_Shelf_LOD1/default/oversample-8.
The shelving-unit target still has four collapsed UV faces. Existing overlaps,
extreme distortion elsewhere and shared-atlas padding remain unresolved.

## Algorithm investigation

**SLIM with an injective seed** is a relevant source-parameterization candidate:
it minimizes flip-preventing distortion energies using a local/global procedure.
It requires suitable initialization; local injectivity alone does not certify
global non-overlap. [Authors' project and implementation](https://igl.ethz.ch/projects/slim/).

The existing standalone `DiskSlim` control was tested on captured kitchen chart
patches, independently of the Unity production solver. Representative countertop
patches improved from intrinsic anisotropy 3581.68 to 1.0013 in double precision
(1.338 after Float32 rounding), and from 778.17 to 1.00001 (3.789 after rounding).
An eight-face backsplash patch reached 1.1534 in double precision but about 20.12
after Float32 rounding and failed the existing Float32 UV validity predicate.
These are local chart results before packing, **not** final atlas or LOD improvements.
Point-connected patches are solved independently, which can change UV continuity.

This confirms two separate tasks: use a better parameterization where appropriate,
and preserve its thin triangles through normalization, placement and storage.
Replacing ARAP alone cannot establish correctness of the final Float32 atlas.
The new probe records original/candidate/rounded buffers and the input hash so
the numerical failure can be reproduced rather than hidden by a summary score.

**SCAF** introduces a surrounding scaffold to enforce global bijectivity during
optimization. It is a candidate when boundary collisions remain after locally
injective parameterization. It was researched, not executed in this corpus.
[Authors' paper](https://cims.nyu.edu/gcl/papers/Scaffold-2017.pdf).

**OptCuts** jointly optimizes seams and distortion; the repository already has
standalone controls and measured seam-quality tradeoffs. For furniture, mandatory
cuts and source/target chart correspondence would need explicit preservation.
No new OptCuts corpus result is claimed here.
[Official implementation](https://github.com/liminchen/OptCuts),
[existing repository study](UV_SEAM_PLACEMENT.md).

**xatlas pack-only** remains separable from chart generation through AddUvMesh and
PackCharts. This permits evaluating improved charts before replacing the packer.
The current 4×/8× comparison gives no evidence for an unconditional pack setting
change. [Official API overview](https://github.com/jpcy/xatlas#pack-multiple-atlases-into-a-single-atlas).

For **newly generated LODs**, successive self-parameterization can preserve maps
during edge collapses. It depends on controlling the simplification sequence;
it cannot be assumed to recover the history of independently authored FBX LODs.
This is a research direction, not an implementation in this change.
[Neural Subdivision, section 4.1](https://www.dgp.toronto.edu/~hsuehtil/pdf/neuralSubdiv_95mb.pdf).

## Next acceptance targets

1. With stored-UV degeneracy checked before frame conversion, preserve thin source
   triangles after packing and Float32 storage, with exact
   geometry/UV0 preservation and both source and downstream target checks.
2. Improve donor selection near seams using geometric and normal compatibility
   plus face-interior evidence. The nearest-point controls demonstrate that simply
   switching to global surface projection is insufficient.
3. Trial a bounded injective parameterization fallback only on eligible patches.
   Require a complete post-pack intersection scan, per-face distortion checks,
   preserved correspondence and no regression across the furniture corpus.
4. Add shared-atlas padding/occupancy and full Forward/Reverse runs before making
   claims about end-to-end lightmap quality or changing production defaults.

These measurements do not select a new production solver or establish that the
listed UV defects have been repaired. Correcting a diagnostic changes which
defects are reported; it does not by itself repair the affected atlas.
