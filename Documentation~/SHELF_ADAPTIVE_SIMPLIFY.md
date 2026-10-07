# Shelf adaptive Simplify validation — 2026-10-06

The supplied wireframe is the `LongGunShelf_close_1` capture
`remesh_20261006_090600_773_68ca7f.bin`. Source: 208 triangles; trimmed solid
voxel output: 11,396 triangles. The saved settings request 1,600 triangles,
maximum error 0.01, resolution 64 and preserve folds. This count is an early
stop, not a maximum-output budget: Simplify stops even when flatter geometry
could be represented with fewer faces. Default `targetTriangles = 0` already
reduces by error alone; the manually saved 1,600 setting overrides it.

## Same-input comparison

Unity 6000.2.6f2 / D3D11, current published native plugin; full source-aware
Simplify and Unwrap. Only the triangle stop changes.

| Requested stop | Final triangles | Simplify time (ms) | Source-to-result RMS (mm) | Source-to-result max (mm) |
| --- | ---: | ---: | ---: | ---: |
| 1,600 | 1,600 | 1,557 | 2.47 | 20.26 |
| 208 | 208 | 1,201 | 2.31 | 20.72 |
| 96 | 96 | 1,098 | 2.19 | 19.08 |
| 48 | 50 | 1,052 | 2.88 | 18.53 |
| 24 | 52 | 1,052 | 2.97 | 20.26 |
| 0 (error only) | 48 | 1,001 | 4.57 | 19.57 |

All six final Simplify and Unwrap outputs have zero boundary edges, duplicate
or degenerate faces, non-manifold/inconsistent edges and disconnected vertex
fans. Requested counts are not guaranteed: error limits, topology retries and
source fitting can stop above a requested count. The non-monotonic 48/24 rows
reflect those existing guards, which remain enabled.

Distances are area-weighted source triangle centroid probes against the result.
The complete CSV also records result-to-source edge-midpoint and centroid
probes. They are sampled measurements, not a certified Hausdorff bound. A source
coordinate unit is interpreted as one metre. These measurements compare the
same input and retained geometry; removed small source pieces are included in
the source reference. Native `maximumError` is a collapse metric, not a promise
that every fitted vertex has this pointwise distance from the source.

The 48-face result removes most edge/bevel density and is visibly close to a
box. It has a larger average source deviation than the 96-face version; users
who need the finer shape can retain a count stop or lower maximum error. No
algorithm replacement or relaxed quality gate is needed for this capture.

## Interface and evidence

Simplify now has **Error only** next to **Stop at triangles**. It sets the
existing stop field to zero. A status line explains whether a nonzero count
will stop further reduction. The per-node log reports actual counts and all
three stop/error values so a topology fallback cannot be mistaken for success
at the requested count. Existing saved settings are retained.

Local ignored `_results~/shelf-planar/` contains the captured input, all six
mesh outputs, settings, `metrics.csv`, Unity replay logs and 14 wireframe PNGs
(source plus six outputs, each at two fixed camera angles). No user model data
is committed. The sweep and renders use the same source and camera for every
comparison.

## Verification of the interface/log change

Both licence-free C# compile variants pass (with and without the FBX exporter);
identifier and tool-dependency guards pass. The combined local Unity selection
ran 253 tests: 252 passed and one failed. The failure is
`RemeshSurfaceRefineTests.DiagonalBoxKeepsCollapseBudgetAndUnwrapsWithoutOverlaps`:
xatlas reports invalid or unmapped vertices on its rotated-box fixture. The
same isolated test fails identically with unmodified `8d2e790` production code.
This existing thin-face unwrap limitation is not resolved by the interface
change, and the test remains enabled. Logs and XML are kept with the gallery.
