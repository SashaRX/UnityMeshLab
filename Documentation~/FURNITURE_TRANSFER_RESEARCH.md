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
have degenerate faces; none have nonfinite/invalid faces. These categories overlap
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

## Compared alternatives

Each row below compares 255 target pairs against default grouped transfer.
“Better” means no measured quality component gets worse within the report's
relative 1e-6 float tolerance and at least one improves; it does not establish
correct correspondence. “Tradeoff” means both improvement and regression occur.
Overlap pair area participates even for small contacts; no diagnostic is suppressed.

| Alternative | Better | Worse | Tradeoff | Same |
|---|---:|---:|---:|---:|
| UV0 nearest | 2 | 180 | 55 | 18 |
| Surface nearest | 0 | 193 | 61 | 1 |
| Surface nearest with normal filtering | 1 | 189 | 62 | 3 |
| Disable source reparameterization | 17 | 158 | 67 | 13 |
| Request internal oversampling 8× | 65 | 61 | 129 | 0 |

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

1. Preserve thin source triangles after packing and Float32 storage, with exact
   geometry/UV0 preservation and both source and downstream target checks.
2. Improve donor selection near seams using geometric and normal compatibility
   plus face-interior evidence. The nearest-point controls demonstrate that simply
   switching to global surface projection is insufficient.
3. Trial a bounded injective parameterization fallback only on eligible patches.
   Require a complete post-pack intersection scan, per-face distortion checks,
   preserved correspondence and no regression across the furniture corpus.
4. Add shared-atlas padding/occupancy and full Forward/Reverse runs before making
   claims about end-to-end lightmap quality or changing production defaults.

This change adds reproducible diagnostics and algorithm evidence. It does not
silently select a new production solver or declare the listed defects repaired.
