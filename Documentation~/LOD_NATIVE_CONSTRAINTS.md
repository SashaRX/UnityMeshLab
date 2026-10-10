# Native crease constraints

Optional **Native Crease Constraints** is available in LOD Gen with Triangles,
Prioritize Triangle Budget and Preserve Hard Edges. It is disabled by default.
The previous strict incident-face belt and managed chain prepass remain controls.

The new additive `meshoptSimplifyConstrained` bridge accepts one Lock/Protect byte
per render vertex. All positional wedges receive consistent flags. Crease
endpoints, junctions, material borders and vertices of ambiguous faces are locked;
degree-two crease vertices protect their attribute discontinuity. Meshoptimizer
can retriangulate neighboring faces without freezing their original triangles.
Ambiguous faces remain frozen, with occurrence and interface checks.
Coincident but disconnected vertex fans are locked consistently across wedges.

Every prepared source crease must remain covered by a discontinuous target edge
with both oriented material sides and its original normal field. Native chain
collapses receive only numerical tolerance. If that check fails, simplification
retries with every crease vertex locked. A second failure uses the existing
strict incident-face belt and reports that backend explicitly. If its protected
face floor exceeds the requested budget, additional relaxed native probes stop.
Source copies remain a last-resort validation fallback. Retry counts include
both additional mesh-simplification attempts.

The optional managed prepass can first shorten chains within its separately
verified bounds; final coverage still measures every original working LOD0
segment. All geometry candidates and attribute correction use working LOD0.
The existing normal/RGBA correction gates and subsequent feature checks apply.
This is sampled quality validation, not a certified surface-error guarantee.

The existing `meshoptSimplify` ABI is preserved. Constraint ABI version 1 requires
fresh native binaries built by `build-native.yml`; older binaries produce a
readable error instead of silently running without constraints. The new native
entry rejects invalid index/flag buffers and nonfinite input before library reads.

Native CTest exercises zero attribute costs, oriented shading-side retention,
locks and invalid-buffer rejection. Unity regression and actual FBX/GPU evaluation
use `-meshlabLodNativeFeatures` alongside the existing budget/hard-edge/chain flags.
All platform builds and both native CTests passed in [CI run 38041076577](https://github.com/SashaRX/UnityMeshLab/actions/runs/38041076577).
The binaries were imported through CI's commit `3513525`; they were not rebuilt or
copied by hand. The initial extended comparison completed every model but exceeded
its 15-minute timeout (182 other checks passed). The final comparison uses a
30-minute timeout, strict-belt fallback and additional matched-count candidates.
