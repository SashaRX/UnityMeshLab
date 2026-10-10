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

Every prepared source crease must remain covered by a discontinuous target edge
with both oriented material sides and its original normal field. Native chain
collapses receive only numerical tolerance. If that check fails, simplification
retries with every crease vertex locked. A second failure retains a source copy
and reports the fallback. Native retry counts include this additional attempt.

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
Measured model results will be recorded after CI rebuild and local Unity testing.
