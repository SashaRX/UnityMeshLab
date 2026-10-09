# Closure topology: paired rims can require Bridge instead of disks

2026-10-09, extending the [Cap plan](REMESH_CAP_PLAN.md) and
[incremental transaction probe](REMESH_CAP_INCREMENTAL.md).
`Tools~/RemeshCapBenchmark/bridge_probe.py` compares known-reference closures
of a torus with a removed circumferential band and a box missing opposite faces.
It adds topology diagnostics and fixtures, not automatic Bridge reconstruction.

## Measured counterexample

The torus fixture has 12 major and 8 minor samples. Removing one major strip
leaves 96 used vertices, 272 geometric edges, 176 faces and two boundary loops
on one connected source component. Both rims pass P2 whole-plane checks.

| Surface | Components | Boundary loops | Euler characteristic | Genus | Closed manifold / no new intersections |
| --- | ---: | ---: | ---: | ---: | --- |
| Open torus after strip removal | 1 | 2 | 0 | 0 | Open |
| Each rim filled with a disk | 1 | 0 | 2 | 0 | Pass / pass |
| Removed strip restored as reference Bridge | 1 | 0 | 0 | 1 | Pass / pass |

Separate disks destroy the handle despite passing local topology, winding,
preservation and exact intersection checks. The current incremental gate also
accepts both supplied disks in sequence; fresh neighbor analysis cannot recover
the lost handle once a disk hypothesis has been chosen. This is a measured limit
of the local gate, not a claimed production regression: production Cap remains
unimplemented.

The reference Bridge reconnects the two rims using the known removed strip.
It preserves all original source positions/faces and has the same triangles as
the original torus, modulo ordering. Its correspondence and triangles are
supplied by the analytic fixture, not discovered by an automatic algorithm.

## Topology alone cannot decide intent

A box missing opposite faces also has one connected component, two boundary
loops, Euler characteristic zero and open genus zero. Separate disks correctly
restore its intended closed genus-zero surface. The open topological signature
is therefore insufficient to choose disks versus Bridge.

For each orientable manifold component the probe counts **used geometric**
vertices, edges and faces after exact position welding, with
`chi = V - E + F` and `g = (2 - boundaryLoops - chi) / 2`. Attribute seams and
unused positions do not alter this signature. Invalid edge topology or singular
vertex fans refuse genus reporting; intersection certification remains separate.
Expected closed genera and component count come from explicit fixture intent.
Never assume that closing a hole must preserve the genus of the already open
source: the open cut torus itself has genus zero.

## Required architecture change

Classify the **closure domain** before selecting per-rim plane patches:

- A disk fills one rim.
- A planar region with inner boundaries spans an outer rim and its holes.
- An annular Bridge connects two rims while preserving the passage/handle
  required by the intended model.
- Compound or junction domains require further structure or explicit refusal.

Group boundary occurrences into operation hypotheses. Same-component membership,
nearby rims and planar fits provide evidence but cannot force a Bridge. Use
collar geometry, surface continuation, rim orientation, scale and intended open
features to compare alternatives; retain ambiguity when evidence is insufficient.
Two separate source components can also be joined by a Bridge, but that changes
component count and needs an explicit reconstruction policy.

Bridge needs its own bounded cyclic correspondence search, seam/phase alternatives
and winding constraints. Unequal rim vertex counts must retain each selected
source edge exactly once; do not move/split original borders to make counts equal
under the current Cap-only preservation contract. Candidate strips must reject
twists, collapse, crossings and overlaps and connect the intended source fans.
Surface quality and normal continuation need additional tests beyond genus.

Disk and Bridge hypotheses have different topological effects: each disk adds
one to Euler characteristic, whereas an annular strip adds zero when glued along
two complete rims. Record proposed patch topology and expected assembled outcome
per branch, then audit the actual result. A same-component annular completion
can add a handle; a between-component completion instead joins components.

Do not commit independent disk closures while a paired-rim hypothesis remains
unresolved. Re-analysis still runs after a tentative accepted step, but it must
respect the selected domain's topology intent and allow rollback of the entire
branch. Global closure checks include components, Euler/genus, geometry and
shape. Synthetic Bridge surfaces remain separate from original bake donors.

## Reproduction and tests

```powershell
python 'Tools~/RemeshCapBenchmark/bridge_probe.py' --output PRIVATE_BRIDGE_REPORT.json
python -m unittest discover -s 'Tools~/RemeshCapBenchmark' -v
```

The strict JSON report records both local outcomes, explicit fixture intent and
the sequential-disk counterexample. CLI exit 0 means the expected counterexample
and reference closures were reproduced; it does not mean automatic Bridge
selection. Private output: `_results~/cap-structure-20261009/torus-bridge.json`.

**86/86 offline tests pass**, including seven new Bridge-probe tests for handle
loss, ambiguous open signatures, source/edge preservation, seams, winding/order,
transforms/scales, invalid signatures and component-aware intent. NumPy 2.1.3 and
Triangle 20250106 are the existing benchmark dependencies. No Unity/native or
production Cap implementation is changed or validated by these Python results.
