# Simplify cleanup validation — 2026-10-06

The log from package `e3fe309f8c4f` shows all six native collapse attempts
rejected on `LongGunShelf_close_1`, both before and after source refinement.
The trimmed input is closed and valid. Capture:
`remesh_20261006_012203_413_7e4691.bin` (voxel 64, target 1600,
maximum error 0.01, preserve folds and prune small parts enabled).

## Cause and fix

Native `Simplify` called `Clean` with a model-relative area floor of
`extent² * FLT_EPSILON`. Four positive-area faces of the 1600-triangle result
fell below the floor. Their deletion opened 12 boundary edges. Reducing the
collapse error did not help; the next two larger targets lost eight and
seventeen faces respectively, also opening the surface.

Cleanup now removes only exactly collapsed faces. Native vertex positions,
collapse error and algorithm settings are unchanged by this fix. The managed
boundary, component, winding and vertex-fan checks remain enabled.
Staged Unwrap uses the same zero-area cleanup limit: charting may split vertices
but may not silently remove a positive-area surface face.

## Reproduction and verification

Direct native replay, with the exact managed retry target sequence:

| Attempts | Target | Old triangles / boundary edges | Fixed triangles / boundary edges |
| --- | ---: | ---: | ---: |
| 0–3 | 1600 | 1596 / 12 | 1600 / 0 |
| 4 | 2849 | 2840 / 24 | 2848 / 0 |
| 5 | 5698 | 5681 / 49 | 5698 / 0 |

First-attempt native collapse error is unchanged: `0.00010516667680349201`.
All fixed outputs have zero non-manifold or inconsistently oriented edges.

Isolated Unity 6000.2.6f2 runs replay the same captured source, trimmed mesh and
settings through the full source-aware Simplify pipeline and then Unwrap:

| Build | Simplify wall time, two runs | Final triangles |
| --- | --- | ---: |
| Previous published native plugin | 2282.5 / 2250.7 ms | 11396 |
| Fixed local native build | 1526.9 / 1551.6 ms | 1600 |

Both complete fixed Simplify and Unwrap outputs have zero boundary edges,
duplicate/degenerate faces, non-manifold edges, winding errors and disconnected
vertex fans. These timings cover Simplify only. They do not establish a general
GPU or bake speedup, and the final mesh differs because collapse now succeeds.

A native regression uses a closed tetrahedron with apex height `1e-8` at scales
1 and 0.001. Its no-op Simplify must retain all four faces and closed oriented
edges. The test fails against the previously published DLL and passes with the
fix. Managed tests exercise the raw simplifier without its fallback, input
immutability, and the guarded stage. Native CTest and targeted Unity topology,
bake, hierarchy and review-regression suites validate integration.

## Remaining limits

An extremely thin positive-area face can be below xatlas's own numerical area
threshold. Staged Unwrap now reports an invalid mapping for such input instead
of succeeding with a hole; it does not invent UVs or deform geometry to bypass
that limit. The full shelf replay above succeeds after the existing source fit.

The first, unnamed 33-second Simplify run in the supplied log has no associated
capture in that attachment. Its cleanup failure is plausible but has not been
reproduced on its exact input. The separate Cooler raw-voxel non-manifold issue
documented in `COOLER_VOXEL_VALIDATION.md` is not fixed by this change.

The same log also reports `Save All` failing to create
`Assets/UnityMeshLab/Output`: both inspected user projects lack its parent.
Folder creation now walks each path component through AssetDatabase. Tests
check missing parents, existing GUIDs, invalid paths and a file blocking a
parent. No user project assets were changed during validation.

Local replay inputs, JSON and Unity logs are in ignored
`_results~/simplify-cleanup/`. Published native binaries must come from
`build-native.yml`; scratch builds are only for local verification.
