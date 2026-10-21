# Transfer quality metrics: definitions and disagreements — 2026-10-21

Item F7 of the transfer analysis in PR #294. Forward transfer judges the same
UV layout several times, at different stages, with different definitions of
"out of bounds", "stretched", "collapsed" and "overlap", and in two different
geometric frames. A layout one stage accepts can be one the next stage rejects,
and the auto-tune score that picks the published attempt ignores some defects
entirely. This document lists the definitions, the decisions each one drives,
and concrete layouts the stages judge differently. It changes no code.

Line numbers refer to main at `7aaecf9`. File prefixes: `GST` =
`Editor/GroupedShellTransfer.cs`, `UTW` = `Editor/Uv/UvTransferWorkflow.cs`,
`FSP` = `Editor/Uv/ForwardUvSeamPreparation.cs`, `UAD` =
`Editor/Geometry/UvAtlasDiagnostics.cs`, `TCQ` =
`Editor/Diagnostics/TransferCandidateQuality.cs`, `TUQ` =
`Editor/Diagnostics/TransferUvQuality.cs`, `ATS` = `Editor/Uv/AutoTuneScore.cs`.
Items marked UNVERIFIED follow from reading the code but were not reproduced.

Related: theme T3 ("absolute epsilons that don't scale") in
`TRANSFER_AUDIT_2026-07-18.md`.

## The four judges

| Judge | Used by | Frame | Precision | Scope |
|---|---|---|---|---|
| `CountShellIssues` (GST:4723) | every legacy candidate choice in grouped transfer: transform vs interpolation, merged passes, `SelectBestCandidate`, the >50% reject gate | mesh local | float | one shell |
| `TransferCandidateQuality` (TCQ) | final-recovery trigger and acceptance, final shell status | mesh local | double, but `issues` comes from `CountShellIssues` | one shell, 200k-comparison overlap scan |
| `TransferUvQuality` (TUQ) | workflow: donor-seam trial trigger and acceptance, auto-tune input, benchmarks | rendered (`ctx.PreviewMatrix`, the TRS chain to the LODGroup root) | double | whole mesh, 2M-comparison overlap scan |
| `AutoTuneScore` (ATS) | picks the published auto-tune attempt and stops the search when `Perfect` | from TUQ | — | all targets |

`UvAtlasDiagnostics` (UAD) is the shared overlap kernel and also counts
out-of-bounds, degenerate and invalid faces for TUQ and the source-atlas gate.

## Out of bounds

| Location | Condition | Decision |
|---|---|---|
| UAD:84 | finite and outside [-1e-4, 1.0001] | TUQ `outOfBoundsVertices`; source-atlas gate |
| GST:2216-2221 | source atlas has any OOB vertex (or overlap, degenerate, invalid face, incomplete scan) | `sourceAtlasRecoveryAllowed = false`: TCQ final recovery is off for every shell of the mesh. Planar recovery does not read this flag |
| TCQ:46-48 | non-finite or outside [0, 1], no epsilon | `Improves` requires no more OOB vertices than the other layout; not part of `NeedsRecovery` or of the final status |
| GST:1071 `uv2OobMargin` | source shell UV2 AABB ± 1.25 px of min(W, H), 0.005 when the atlas size is unknown | merged-shell coherence fix; transform candidate clamped and charged one issue per OOB vertex |
| GST:1072 `uv2BoundsTolerance` | [0, 1] ± 2.5 px of min(W, H), 0.01 when unknown | partition and full transform candidates rejected |
| GST merged safety net (after the shell loop) | source shell UV2 AABB ± 20% of its size | outlier vertices re-projected through UV0, no acceptance check |
| GST overlap composite and 3D reprojection | ± 50% and ± 30% of the source AABB, 2× its area or diagonal | composite rejected or face replaced |
| ForwardUvSurfaceProjection.cs:85 | no OOB vertex in the whole candidate mesh | surface recovery candidate refused |
| UTW publication clamp, XatlasRepack | strict [0, 1] | clamped when `ClampLightmapToUnit`; TUQ re-measured only for auto-tune |
| `Editor/TransferValidator.cs`:251 | ± 0.01 | diagnostics overlay only |

Grouped transfer divides its pixel margins by min(W, H); planar recovery and
seam preparation divide by max(W, H).

## Stretch

Three implementations compute the same quantity, σmax/σmin of the 3D → UV
Jacobian: `GroupedShellTransfer.TriangleStretch` (float, local positions),
TUQ `JacobianAnisotropy` (double, scale-normalised, rendered frame) and
`ReverseUvTransfer.TriangleAnisotropy` (double, explicit frame).

| Location | Threshold | Frame | Decision |
|---|---|---|---|
| GST:4744-4745 | > 10,000 counts as an issue | local | every legacy choice and the reject gate |
| TCQ:33, 77 | `stretchedFaces` if > 4; `NeedsRecovery` if worst > 4 | local | final recovery trigger |
| TCQ:67-74 | worst and area-weighted may grow ≤ 1%; improvement needs < 0.99× | local | final recovery acceptance |
| GST:3966 | status issues = max(issues, stretched(> 4) + invalid) | local | Accepted / Degraded / Poor |
| TUQ:63 | `stretchedFaces` if > 1.25 | rendered | benchmarks only |
| UTW:2520 | worst > 4 (or overlap pairs, or degenerate faces) | rendered | starts the donor-seam trial |
| UTW:2539-2552 | worst ≤ 4 with no defects, or `MetricFits` plus a 1% area-weighted gain | rendered | accepts the prepared mesh |
| FSP:239-241 | donor face kept if anisotropy ≤ 4 | rendered, but the **target's** frame applied to source faces | donor filter |
| ATS | no stretch term | — | — |

## Collapsed, degenerate and invalid faces

| Location | Definition | Decision |
|---|---|---|
| GST:4739 | missing vertex, or \|2A\| < 1e-10 in UV (absolute) | issue |
| GST:4745 | NaN or infinite UVs fail both comparisons and are **not** counted | — |
| TCQ:25-31 | invalid = missing or non-finite UV; zero 3D area skips the stretch term | recovery |
| TUQ:49-60 | degenerate = zero 3D length or area in the rendered frame, or an exactly singular Jacobian; invalid = non-finite | trigger, acceptance, auto-tune |
| UAD:101 | degenerate = UV area ≤ 1e-16 (skipped by the overlap scan) | source-atlas gate, exact-family gates |
| ATS:58 | collapsed = invalid + max(0, degenerate − zero-3D-area faces) | auto-tune rank 3 |
| ReverseUvInputs.cs:68 | faces with exactly zero local area are removed from seam-prepared meshes | changes published topology |

## Overlap

The kernel (UAD:199) counts a pair when the clipped intersection area exceeds
max(1e-16, 1e-8 × the smaller triangle). `pairAreaSum` adds pair areas; it is
not a union.

| Location | Scope and budget | Decision |
|---|---|---|
| TUQ:33-35 | whole mesh, 2M | trigger, acceptance, auto-tune |
| TCQ:54-59 | own shell only, 200k | recovery; an incomplete scan blocks `Improves` |
| GST:3967 | own shell; pairs or an incomplete scan add one issue | status |
| GST:2216-2221 | whole source atlas, 200k | recovery gate |
| GST candidate AABB checks | UV2 bounding boxes, not triangles | merged and transform candidates rejected |
| GST force3D report (after the safety net) | force3D shell AABB against other shells' AABBs | `shellsOverlapFixed`; auto-tune `Perfect` requires 0 |
| PlanarTransferRecovery, FSP, ForwardUvRejectedCuts | whole mesh, 2M; any new contact or an incomplete scan reverts | recovery |

## Frames

- **Local positions:** all of `GroupedShellTransfer`, TCQ,
  `DescribeForwardProjection`, `PlanarTransferRecovery` (despite its
  "planar-world" trace label), the exact-family projection and
  `TransferValidator`.
- **Rendered frame:** TUQ in the workflow, the auto-tune zero-area count, and
  the seam preparation metrics.

## Disagreements

1. **Finite stretch is invisible to grouped transfer and auto-tune.** A shell
   with one face at 50:1 (or 1e6:1) has no issue below 10,000:1, so every
   legacy choice treats it as clean. Its final status (TCQ, > 4) is Degraded,
   the workflow (TUQ worst > 4) starts the seam trial, and auto-tune has no
   stretch term, so it can call the attempt `Perfect` and stop the search.
2. **Tiny well-shaped UV triangles count as collapsed.** Legs of about 5e-6 UV
   give \|2A\| < 1e-10: an issue for grouped transfer, so a merged shell made of
   them can be rejected. TUQ normalises the Jacobian (anisotropy ≈ 1) and UAD
   needs area ≤ 1e-16, so the workflow and auto-tune see a clean layout. The
   absolute threshold depends on atlas scale, not on shape.
3. **Local versus rendered frame.** With a renderer scale of (1, 1, 6), a face
   in the XZ plane with isotropic local UVs is Accepted by grouped transfer
   (anisotropy 1) and starts the seam trial in the workflow (6 > 4). Final
   recovery can also replace a layout that is isotropic when rendered with one
   that is isotropic only locally.
4. **Per-shell versus whole-atlas overlap.** Two target shells that share a
   source are each clean in their own scan and both Accepted, while the
   whole-mesh scan reports the pairs, starts the trial and charges auto-tune.
5. **Bounding boxes versus triangles.** An L-shaped force3D shell whose box
   meets a neighbour's box without any triangle overlap increments
   `shellsOverlapFixed`, so auto-tune can never be `Perfect`, although TUQ
   finds no overlap. The same box tests reject valid merged and transform
   candidates.
6. **Out-of-bounds tolerances.** A vertex at 1.00005 counts for TCQ but not for
   UAD or TUQ. A vertex at 1.001 in a 1024 atlas passes grouped transfer's
   2.5 px tolerance, counts in TUQ, is ignored by the trial trigger and by
   auto-tune, and makes surface recovery refuse every candidate for that mesh
   (it requires zero OOB vertices in the whole mesh, including faces it does
   not replace). A source vertex at 1.0002 turns final recovery off for the
   whole mesh while planar recovery still runs.
7. **Zero-area 3D faces.** TUQ counts them as degenerate, so the trigger at
   UTW:2520 starts the seam trial for a mesh whose UVs are perfect; auto-tune
   subtracts them. Seam preparation removes exactly zero-area faces, so the
   prepared mesh can reach zero degenerate faces and be accepted for the
   deletion alone (UNVERIFIED end to end). This is the zero-area case of F5.
8. **Scan budgets.** A shell large enough to exhaust its 200k-comparison scan
   gets one issue and a Degraded status while the 2M whole-mesh scan is
   complete and clean. The trial trigger ignores an incomplete scan; auto-tune
   ranks incomplete scans second.
9. **Unknown atlas size.** Planar recovery uses zero tolerance (exact
   coverage), seam preparation a 16 px floor, grouped transfer fixed UV
   fallbacks of 0.005 and 0.01.
10. **Non-finite UVs** are not issues for grouped transfer (comparisons with
    NaN are false), so the issue-only choices cannot see them; TCQ and TUQ
    count them as invalid.
11. **One name, two thresholds:** `stretchedFaces` means > 1.25 in TUQ
    (benchmarks, Reverse audit) and > 4 in TCQ (shell status).
12. **Rejected shells.** A merged shell the reject gate leaves at (0,0) keeps
    its merged method, so the AABB safety net still re-projects it vertex by
    vertex when its source has a UV0 BVH and the origin lies outside the
    allowance. Its status stays `Rejected`, auto-tune counts it as unwritten,
    and no stage measures the layout it actually has (PR #310, PR #305).
13. **Donor frame.** Seam preparation filters donor (source) faces by
    anisotropy in the target renderer's frame (FSP:239-241). This is wrong
    when the source and target renderers have different transforms
    (UNVERIFIED in practice).

## Proposed order of fixes

Each item changes which layout wins, so each is its own PR gated by the
furniture corpus (`report.py --baseline … --require-no-regressions`).

1. **Auto-tune stretch and OOB terms** (1, 6). Auto-tune publishes the result;
   a worst-anisotropy and an OOB term after collapsed faces would stop it from
   calling a stretched or out-of-bounds attempt perfect.
2. **Relative collapse in `CountShellIssues`** (2, 10). Count a face as
   collapsed by its Jacobian (as TUQ does) instead of an absolute UV area, and
   count non-finite UVs. This moves the reject gate, so it follows the
   rejected-shell decision.
3. **Rejected shells and the safety net** (12). Decide whether the net may
   touch rejected shells; measure first how many it re-projects (queued in
   PR #294).
4. **One OOB predicate** (6, 9). A single helper with one epsilon for "inside
   the unit square", and surface recovery judging only the faces it replaces.
5. **Exact overlap for `shellsOverlapFixed`** (5). Reuse the triangle kernel
   instead of bounding boxes for the auto-tune counter.
6. **Frame** (3, 13). Pass the rendered frame into grouped transfer's
   quality measures, and use the source renderer's frame for donor faces.
7. **Names** (11). Rename TUQ's `stretchedFaces` to make the 1.25 threshold
   explicit. It is serialised in benchmark rows, so reports change too.
