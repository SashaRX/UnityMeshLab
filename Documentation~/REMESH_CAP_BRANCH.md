# PileOfBricks: Cap branch reconnection

Measured 2026-10-09, continuing the [compound Cap experiment](REMESH_COMPOUND_CAP.md)
on the same private input and native af94e5860826. This changes offline diagnostics
only. Production Cap/Remesh, source assets, native binaries and the live Unity
project remain unchanged.

## Why the remaining branch was disconnected

At geometrically welded source slot 1228 the refined candidate has two closed
face fans, despite valid edge incidence. The original source fan endpoints are
1298/1133 and 1327/1207. The frozen directed boundary cycles instead run
1133 -> 1228 -> 1207 and 1327 -> 1228 -> 1298. A boundary cycle decomposition
at a branching vertex does not by itself encode the actual 3D source face link.
Compound projected triangulation closes the cycles but can leave disconnected
geometric vertex fans. Coincident vertex-index duplication cannot satisfy the
managed topology contract, which welds by position.

## Controlled Cap-only connection

The optional `--bridge-branches` operation in the benchmark removes one Cap
triangle from each of two fans. Given oriented triangles `(v,a,b)` and `(v,c,d)`,
it inserts `(v,a,d)`, `(v,c,b)`, `(a,b,d)`, `(c,d,b)`. This preserves the oriented
patch boundary, all source faces and every position. There is no displacement
epsilon and no relaxation of intersection tests.

Each trial must reduce disconnected fan count without introducing boundary,
duplicate, degenerate, non-manifold or inconsistently oriented edges. Exact 3D
tests then reject any new patch/source, patch/Cap or patch/patch intersection
beyond an allowed shared feature. The stable search accepts the first passing
pair and records refusals, bounded at 256 pairs. It is not a shape optimizer;
more than two fans and defective edge manifolds are outside this operation.
After a change, the complete candidate is audited again.

The private six-pair sweep finds three geometrically clean connections. The
published deterministic search chooses Cap faces 5364 and 5378. Two triangles
become four, so the candidate has **2,936 vertices / 6,012 faces**, including
**1,303 Cap faces**. The original 4,709 faces retain their positions and order.
The CLI capture is byte-identical to the native-probed sweep candidate.

## Acceptance and actual Unity output

The exact full audit checks 49,429 broad-phase pairs. There are **zero new
crossing, overlap or nonadjacent contact pairs**. Actual Unity 6000.2.6f2
`RemeshTopology.Inspect` finds zero boundary edges, duplicates, degenerates,
non-manifold edges, inconsistent edges and disconnected vertex fans in the input.
The remaining fan count falls **1 -> 0**.

However, the four original source/source crossings remain. Consequently
`capAccepted=true` but `solidCandidateAccepted=false`; the compound CLI exits
**2**. A clean Cap must not turn an intersecting source into a certified solid.
This new flag is a topology/intersection gate, not a component-volume, shape or
bake-quality certificate.

All three clean sweep variants give the same defect counts at resolution 128.
The chosen candidate was also run through the actual managed native wrapper,
Trim and conservative fin cleanup in an isolated Unity project:

| Stage | Solve | Faces | Duplicate excess faces | Non-manifold edges | Disconnected fans |
| --- | --- | ---: | ---: | ---: | ---: |
| Raw and Trim | off | 46,276 | 20 | 47 | 76 |
| Raw and Trim | on | 46,276 | 21 | 48 | 77 |
| Fin cleanup | off | 46,236 | 0 | 12 | 31 |
| Fin cleanup | on | 46,234 | 0 | 12 | 31 |

These counts match the previous fan-defective compound candidate. The output
position/index arrays and exact triangle multisets do differ; matching aggregate
counts are not a claim of identical output geometry. Trim preserves all raw
faces. Fin cleanup still fails the topology contract. All listed outputs have
zero boundary, degenerate and inconsistent edges.

## Verification and next experiment

**29 offline tests** cover the previous predicates and compound cases, plus a
synthetic two-tetrahedron branch: accepted Cap-only reconnection under both
windings, source/position preservation, full intersection checks, refusal when
all connections intersect, bounded refusal, protected source fans, idempotence,
open-edge rejection and attribute-split rejection. An end-to-end audit test
also refuses the solid gate for intersecting original tetrahedra despite a
separate geometrically clean Cap and closed manifold topology.

Private captures and logs are ignored under `_results~/pile-remesh-20261009/branch/`
and `branch-published/`. Reproduction options are in the
[benchmark README](../Tools~/RemeshCapBenchmark/README.md). These probes do not
save, restart or modify the live E: scene. No production Cap default is changed.

The next isolation step is the original four crossings: on a separate private
source-preparation candidate, repair the implicated source patch explicitly and
measure geometric deviation, winding, full intersections and topology before
running Remesh again. Source retriangulation must remain separate from Cap-only
closure. Only after a geometrically clean input exists can native residuals be
isolated reliably; an offending voxel neighbourhood should then become a small
regression fixture before native topology changes.
