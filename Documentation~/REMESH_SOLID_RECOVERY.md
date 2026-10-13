# Recovering singular solid voxel output

## Failure and cause

Park_Bench_A's prepared Automatic Cap support is a closed, oriented manifold:
2,472 faces, no duplicates, degenerate faces, singular edges or split vertex
fans. Closing the source holes was successful. At voxel resolution 64, the
ordinary meshoptimizer corner extractor produces 2,260 faces with 176 duplicate
faces, 218 non-manifold edges and 204 disconnected vertex fans (Solve off).

Every duplicate is an opposite-winding pair. Removing all 352 paired faces
still leaves six edges with four incident faces and twelve disconnected vertex
fans. Fin removal alone cannot repair this output. Solve on/off and increasing
resolution through 80, 96, 128, 192 and 256 do not make the ordinary output valid.
At 256, even complete pair removal leaves two disconnected vertex fans.

The reviewed [meshoptimizer v1.3 extractor](https://github.com/zeux/meshoptimizer/blob/v1.3/src/remesher.cpp)
connects occupied voxel corners using one sampled/solved position per occupied
voxel. Thin features can therefore produce coincident opposed patches or sheets
sharing a grid edge/vertex. These defects precede Trim, Simplify and UV generation;
they are unrelated to Cap refusal or a failed UV projection.

## Recovery order

1. Run the existing extractor with the requested resolution and Solve setting.
2. Inspect geometric topology. Accept only nonempty, closed, valid solid output.
3. Keep the existing conservative coincident-fin cleanup, including its complete
   topology and component-volume audit.
4. If Solve was enabled, retry the ordinary extractor without source fitting.
5. If ordinary output is still invalid, try occupancy extraction at the same
   resolution. Audit its closed topology and every component's nonzero volume.
6. Continue through the existing Trim, Simplify, surface fitting, unwrap and Bake
   stages only after acceptance. Capture and stop if recovery is invalid too.

Explicit two-sided shell mode retains its existing behavior. Valid ordinary
results and successful unfitted/fin repairs never invoke the new native entry
point. No settings, source mesh, imported FBX or bake donor is modified.

## Occupancy extraction

The native rescue uses the same source rasterization, grid transform and solid
flood fill as the ordinary remesher. It extracts the 0.5 isosurface of binary
occupancy on six tetrahedra per cell. All cells share one body diagonal and
consistent face subdivisions. Intersections occur at occupied/empty edge
midpoints; no sample equals the isovalue. This avoids zero-thickness opposing
patches and ambiguous edge/vertex joins at exact grid positions.

Internal option `1 << 29` selects this reviewed extraction in `remesher.cpp`.
The additive `meshLabVoxelRemeshManifold` entry point returns the existing indexed
position handle and uses the existing copy/destroy functions. ABI 3 layouts and
ordinary entry points are unchanged. Resolution remains 4–1024, and both native
count/emission passes share the same cases. The five-million-triangle budget is
checked before allocating the corner output. No new grid size retry is added.

The rescue trades surface adherence and triangle count for a valid occupied
solid. It does not apply native quadrics/source fitting. Geometry is quantized
to occupancy midpoints and can be thicker/rounder on a coarse grid; voxelization
can also join nearby authored components or change handles. These are voxel
resolution effects, so acceptance does not assert preservation of the source's
component/Euler signature. The usual later source fitting remains available.
The warning explicitly identifies recovery and its unfitted surface.

`RemeshNative.VoxelRevision` is included in the Remesh stage key so a cached
ordinary result cannot stay current after this algorithm changes. Prepare/Cap
keys are independent. Native binaries must be rebuilt with `build-native.yml`;
an editor that still has the old DLL loaded must restart before using recovery.

## Reproduction and verification

### Voxel revision 2: source-space position

Revision 1's occupancy midpoint formula accidentally added the rasterizer's
padding instead of removing it, translating recovered vertices by one voxel
along every axis. Rasterization puts source cell `x` at padded index `x + 1`,
so padded grid index `g` represents source center `g - 0.5`. Revision 2 corrects
only this coordinate mapping and invalidates cached Remesh stages. Ordinary
extractors, occupancy, triangles, topology guards and resolution are unchanged.

The eighteen disconnected box/thin-plate controls additionally compare every
component's bounds and center with authored geometry. Centers may deviate by
at most half a voxel (plus rounding allowance); bounds by one voxel. This
position check fails on the prior formula, independently of topology validity.
Native binaries must be rebuilt by CI. Restart if the Editor still binds the
previous DLL; checking the loaded midpoint coordinates distinguishes it from
the corrected binary. Both live E/C editors load the CI binary with SHA-256
`C72835487AD639C2A05624618E9D69C7549DFA11FE696CEB163504C55BF5EB59` and pass
the centered-cube probe after package/domain reload, without saving their scenes.

Fresh voxel-2/surface-fit-2 Unity controls pass Park/Garbage with Solve off/on:

| Capture | Solve | Simplified faces | UV islands | Mean/worst stretch | Target/source RMS |
|---|---|---:|---:|---:|---:|
| Park_Bench_A | Off | 2,926 | 348 | 1.043 / 3.380 | 0.020595 / 0.016042 |
| Park_Bench_A | On | 2,926 | 79 | 1.099 / 6.071 | 0.011839 / 0.008501 |
| Garbage_Chute | Off | 68 | 4 | 1.031 / 1.292 | 0.019061 / 0.063717 |
| Garbage_Chute | On | 68 | 5 | 1.024 / 1.355 | 0.016433 / 0.058543 |

All four UV scans are complete with zero overlaps, degenerates and out-of-bounds
vertices. Park's Solve-on target RMS improves from 0.040565 to 0.011839, but
worst UV stretch increases from 3.121 to 6.071. These measure different properties;
closer geometry does not certify better UV quality. Garbage is unchanged.
The final regression selection passes 322 tests, zero failures, 16 optional
private-input skips. Evidence: `_results~/cafe-chair-20261010/revision2-regression/`.

The measurements below are historical revision-1 results; corrected geometry
can change later Trim, source fitting, Simplify and UV results, so those numbers
must not be treated as revision-2 acceptance evidence.

Private local captures remain outside the package in `_results~`. Baseline and
resolution reports are under `_results~/voxel-solid-20261010/`. The prepared
inputs come from `_results~/bridge-budget-20261010/comparison/`.

Direct native replay of the new extractor on Park_Bench_A yields closed valid
surfaces at every tested resolution: 64, 80, 96, 128, 192 and 256. At 64 it emits
15,888 faces, with zero duplicates, degenerate faces, open/singular edges,
winding errors or disconnected vertex fans. This is intermediate geometry,
before Simplify.

The native regression fixture combines a closed box with a disconnected
one-voxel-layer thin plate. Eighteen cases cover resolution 32/64, all three axis
permutations and scales 0.001/1/1000. They require unique positive-area faces,
closed consistently directed edges, connected vertex links and two positive
component volumes, including the thin plate. Existing native remesh and UV input
tests remain enabled. Both CTest targets pass locally.

Managed tests cover recovery ordering, one-attempt budgeting, unchanged accepted
and shell results, rejection of open and zero-volume candidates, cancellation,
donor preservation and native Voxelize→Simplify→UV on the thin plate. The private
`VerifyAuditedNativeCandidates` test is an acceptance gate; the separate
comparison exporter deliberately treats failed pipeline rows as research data.

The Unity 6000.2.6f2 selection produced 196 passes and 16 optional-input skips.
One historical test expected refusal of a measured open voxel union of two
intersecting, closed donors. Recovery now succeeds, so that test was replaced
with two Solve-off/on acceptance cases requiring closed topology, nonzero volume,
successful Simplify/UV and unchanged prepared donors; both pass. The final
selection therefore contains 198 passed checks and 16 skips, verified in the
main run plus the two-case follow-up. No topology check was removed. Both FBX
define builds pass, and all 130 offline benchmark tests pass.

The private acceptance gate also passes both captured models through
Voxelize→Trim→Simplify→Unwrap at resolution 64, with Solve off/on:

| Capture | Solve | Voxel faces | Simplified faces | UV islands | Mean/worst stretch | Overlap / degenerate / outside |
|---|---|---:|---:|---:|---|---|
| Park_Bench_A | Off | 15,888 | 2,946 | 355 | 1.039 / 2.982 | 0 / 0 / 0 |
| Park_Bench_A | On | 15,888 | 2,946 | 112 | 1.038 / 2.851 | 0 / 0 / 0 |
| Garbage_Chute | Off | 6,144 | 68 | 4 | 1.031 / 1.292 | 0 / 0 / 0 |
| Garbage_Chute | On | 6,130 | 68 | 5 | 1.024 / 1.355 | 0 / 0 / 0 |

These frozen captures validate geometry and UV, not material Bake coverage or
visual parity. Park_Bench_A's coarse recovered rods are thicker and its atlas is
still fragmented. Better surface adherence and island merging are separate work;
this recovery removes the topology failure, not every low-resolution quality
limitation. All Windows/Linux/macOS native CI builds and tests passed, and the
CI-published binaries were used for the Unity acceptance gate.
