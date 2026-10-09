# Incremental local Cap: continuity, audit and fresh boundary analysis

2026-10-09, following [plane hypotheses](REMESH_CAP_PLANES.md).
`Tools~/RemeshCapBenchmark/incremental.py` provides an offline transactional
gate for **supplied planar proposals**. `incremental_probe.py` replays known
box reference faces in different orders. Automatic P3 feature reconstruction,
triangulation policy and a production Cap stage are still unimplemented.

## One tentative step

1. Extract boundaries from the current working geometry. A support arc must
   belong to that exact snapshot, contain distinct boundary halfedges and follow
   their successor order, geometric endpoints and source fans continuously.
   Wrapped arcs are supported; jumps between cycles and unresolved singular
   junctions are refused. Spatial endpoint proximity alone is insufficient.
2. Append proposed vertices and triangles without moving or rewriting protected
   positions or faces, including previously accepted Caps. Match source dtypes.
   Fit the support plane with P2's rank and maximum-residual policy, then check
   all proposal vertices against it. Refuse unused new vertices.
3. Re-extract topology. Duplicate/degenerate faces, inconsistent edge orientation
   and non-manifold edges are refused. Every selected old boundary occurrence
   must disappear exactly once; all unselected old occurrences must remain.
   New boundary edges may belong to the proposal, such as a shared missing-face
   edge. Disconnected fan counts cannot increase at any position. Every added
   face must attach over added-face edges to the selected arc; detached islands
   are refused.
4. Run bounded AABB broad phase and the existing exact triangle classifier for
   new/old and new/new pairs. Crossing, positive-area coplanar overlap and
   improper nonadjacent contact reject the step. Adjacent triangles are allowed
   only when their entire intersection stays on their shared feature.
   Previously accepted Caps are protected old geometry on the next step.
5. Recompute plane hypotheses for the changed geometry. Do not reuse old loop
   IDs or hypothetical arcs: snapshot identity and source fans have changed.
   If re-analysis or cancellation fails, return unchanged pre-step arrays.
   Only a fully audited step returns the new working arrays.

Default transaction limits: 8,192 total faces, 16,384 total vertices and 50,000
exact pair tests. P2 search budgets also apply to re-analysis. Cancellation is
checked between stages and pair tests; NumPy bulk operations are not
interruptible. Invalid array/options inputs raise an error without modifying
source arrays; an audit refusal returns `accepted=false` and unchanged arrays.
There is no Unity scene/asset mutation.

## Known-reference measurements

| Missing faces | Orders replayed | Plane sequence after successive steps | Final reference result |
| --- | ---: | --- | --- |
| Two adjacent | 2 | One whole-plane rim, then no boundary | Same geometry in both orders |
| Three adjacent | 6 | Unique two-plane rim, then one whole-plane rim, then no boundary | Same geometry in all six orders |

The three-face fixture starts with seven vertices, so its missing corner is
not hidden in the input. The first supplied reference face introduces it.
Each final box independently passes closed-manifold and exact Cap intersection
audits and preserves its original position/face prefix.

This supports re-analysis after a step: a formerly uncertain neighbor can become
simpler when the accepted proposal supplies a finite common edge. It does not
demonstrate automatic corner discovery or prove greedy closure safe on arbitrary
models. Reference coordinates and triangles come from the known box, not from
a missing-feature solver.

## Branching and global acceptance still required

A locally valid Cap can have the wrong intended shape. Keep working geometry
per hypothesis branch, proposal provenance and pre-step snapshots. If neighboring
completion fails, roll back that branch and try another feature graph or order
under a bounded search. Existing source intersections are unchanged and excluded
from this gate's new-pair audit; global source readiness is a separate requirement.

`accepted=true` means a supplied step passed local continuity, plane, coverage,
topology, intersection and re-analysis gates. Reports explicitly retain
`intendedShapeCertified=false` and `solidCertified=false`. An easier remaining
plane fit is not a shape certificate. Selected old occurrences decrease strictly,
but new feature edges can leave total boundary count unchanged; global termination
needs a step/search budget, not that count alone.

For production, publish only after the complete intended closure passes shape,
topology, intersection and source-readiness checks; deliberate partial closure
must be explicit. Keep original bake/material donors separate from changing
working support geometry. Caps constrain adjacent geometric completion without
becoming implicit original UV/material donors.

## Reproduction and tests

```powershell
python 'Tools~/RemeshCapBenchmark/incremental_probe.py' --output PRIVATE_INCREMENTAL_REPORT.json
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
```

The CLI writes strict JSON with each order, step, fresh plane summary, audit
witnesses and final independent reference audit. Exit 0 means all supplied
reference replays passed; exit 2 means a failed reference replay. Private output:
`_results~/cap-structure-20261009/incremental-boxes.json`.

**79/79 offline tests pass** with NumPy 2.1.3 and Triangle 20250106 for existing
compound tests. Twelve new incremental test methods cover all box orders,
snapshot identities, fan continuity, preserved prefixes, transforms/scales,
rollback on crossings/overlaps, new/new and improper adjacent contacts,
unselected openings, detached faces, winding, nonplanar proposals, budgets,
re-analysis refusal and cancellation. These are Python benchmark results,
not Unity EditMode, native Remesh or bake validation.
