# Generated annular Bridge and virtual averaged co-normal growth

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
