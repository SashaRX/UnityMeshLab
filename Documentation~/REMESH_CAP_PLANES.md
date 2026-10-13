# Cap P2: whole-rim and contiguous-arc plane hypotheses

2026-10-09, continuing the [architecture plan](REMESH_CAP_PLAN.md) and
[source-fan-aware boundary extraction](REMESH_BOUNDARY_EXTRACTION.md).
Implemented in `Tools~/RemeshCapBenchmark/planes.py`. This offline stage
generates no vertices, edges or faces and preserves original source arrays.

## Fit and evidence

Analyze a fresh P1 extraction, retaining original corner occurrences and
snapshot-bound loop selection. First fit the whole rim in a centered, scaled
float64 frame with arc-length endpoint weights. Open arcs use only their actual
edges: no artificial closure chord contributes to the fit. Weighted SVD checks
non-collinear support; normalized second/first singular-value ratio must exceed
`1e-6`. Normal signs and in-plane bases are deterministic.

The default geometric tolerance is `max(absoluteTolerance,
relativeTolerance * boundaryBboxDiagonal)`, with defaults zero and `1e-5`.
A separately reported float64 allowance is `64 * epsilon * bboxDiagonal`.
Maximum point-to-plane distance must pass, not just RMS. A tolerated whole-plane
rim also passes the existing projected crossing/contact checks. This establishes
a plane hypothesis, not a triangulated surface or a solid.

Singular P1 junctions remain `junction_resolution_required`, even if their
whole-rim fit is planar. Do not flatten them into a polygon or pass them directly
to the compound triangulator.

## Partial-plane search

If the whole fit fails, generate candidate junctions at boundary turns of at
least 20 degrees. Refit every complete contiguous chain between these junctions,
including wraparound; chains need at least two edges. Do not stop chain growth
at the first unsuccessful shorter fit. Search for complete cyclic coverage,
with at least 20 degrees between neighboring planes.

This conservative profile searches two through four planes by default and
stops at the smallest supported count. That is a complexity prior, not proof
of the intended missing surface. Rank alternatives by summed RMS and stable
halfedge keys; retain eight by default and report the full hypothesis count and
truncation. `--min-planes` can request higher complexity if P3 later rejects all
lower-count completions. Small turns, shallow creases and curved rims may remain
unsupported rather than being forced into many faces.

Work limits are 512 edges per selected loop, 50,000 fits and 100,000 search states.
Plane counts are limited to 32 to bound recursion. Budget/cancellation refusal
marks analysis incomplete and publishes no partial current-loop hypothesis;
earlier completed loops can remain diagnostic output. P1 has its own extraction
limits. NumPy bulk operations themselves are not interruptible.

## Known-box measurement

The [box probe](../Tools~/RemeshCapBenchmark/box_structure_probe.py) now reports
inferred plane hypotheses alongside its independently audited known reference
closures. Reference faces still come from the box definition, not from P2.

| Missing faces | Plane result | Supported alternatives |
| --- | --- | --- |
| One | Whole rim planar | One plane |
| Two opposite | Two separately planar rims | One plane per rim |
| Two adjacent | Unique two-plane partition | Two arcs of three edges |
| Three meeting at a corner | Ambiguous three-plane partitions | Two alternatives, each with three arcs of two edges |

In the three-face case one alternative lies on the missing `x=+1, y=+1, z=+1`
faces; the other lies on the existing `x=-1, y=-1, z=-1` faces. Both fit their
boundary arcs exactly. Plane residuals alone therefore cannot choose the Cap.
P3 must check occupied source geometry and reconstruct finite shared edges and
the absent corner. Closing each arc by a chord would skip that reconstruction.

## Private PileOfBricks replay

The same exact 4,709-face source produces seven P1 cycles. P2 completes with
253 fits and 214 search states under the default profile:

| Loop edges, in extraction order | Result |
| ---: | --- |
| 203 | Junction resolution required |
| 10 | No supported complete piecewise hypothesis |
| 19 | No supported complete piecewise hypothesis |
| 6 | Whole-plane hypothesis |
| 3 | Whole-plane hypothesis |
| 10 | No supported complete piecewise hypothesis |
| 4 | Whole-plane hypothesis |

These are three planar hypotheses, three unsupported loops and one unresolved
junction. No source intersection was repaired and no new native Remesh result
was generated. Private source and reports remain ignored scratch files:
`_results~/cap-structure-20261009/pile-planes.json` and `p2-box-probe.json`.

## Reproduction and validation

```powershell
python 'Tools~/RemeshCapBenchmark/planes.py' --source SOURCE.bin --output PRIVATE_PLANE_REPORT.json
# Optional selection uses loop IDs from the same snapshot's P1 report:
python 'Tools~/RemeshCapBenchmark/planes.py' --source SOURCE.bin --select LOOP_ID --output PRIVATE_SELECTION_REPORT.json
python 'Tools~/RemeshCapBenchmark/box_structure_probe.py' --output PRIVATE_BOX_REPORT.json
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
```

Every Options field is exposed as a CLI option, with underscores replaced by
hyphens. Exit 0 and `analysisComplete=true` mean selected loops were evaluated,
including ambiguous/unsupported outcomes; they do not mean Cap acceptance.
Exit 2 denotes extraction or work-limit refusal. Invalid inputs/options/selection
raise an explicit error. JSON records fits, tolerances, support occurrences,
alternatives, selection and refusal; `facesGenerated` is always zero.

**67/67 offline tests pass**, with NumPy 2.1.3 and Triangle 20250106 for the
existing compound tests. Fifteen new P2 test methods cover strict JSON replay,
box alternatives, curved/collinear/crossing controls, source-fan junctions,
rotation/translation/scale, winding/order, uneven subdivisions, maximum-error
outliers, tolerated noise, source preservation, selection/truncation, higher
plane counts, budgets and cancellation. Box and private-source CLIs pass.

These are Python tests, not Unity EditMode or attribute-channel validation.
Production Cap remains unimplemented. The next milestone is P3 feature
completion with source-occupation, conditioning and winding checks, followed
by patch triangulation and independent geometric/topology audits.

The [incremental follow-up](REMESH_CAP_INCREMENTAL.md) now audits supplied
planar proposals and re-evaluates neighboring hypotheses after each accepted
step. It tests continuity, local coverage, exact intersections and rollback;
it does not infer the missing feature geometry.
