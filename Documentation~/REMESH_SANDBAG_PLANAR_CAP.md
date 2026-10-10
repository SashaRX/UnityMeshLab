# SandbagRoundedcorner: open floor versus solid voxel topology

2026-10-09, Unity 6000.2.6f2, native library from package `e1de6e9`.
This is measured offline geometry preparation, not a production Remesh fix.

## Exact reproduction

The failure capture records node `SandbagRoundedcorner`, resolution 64, solid
mode, initial flags 1 (Solve) and retry flags 0. Its source contains 685 vertices
and 945 faces. Native replay of both attempts reproduces captured positions and
indices exactly. Trim and Simplify had not run when the guard refused them.

| Geometry / native attempt | Boundary edges | Duplicate faces | Non-manifold edges | Disconnected vertex fans |
| --- | ---: | ---: | ---: | ---: |
| Original source | 47 | 0 | 0 | 0 |
| Unprepared source, resolution 64, Solve | 0 | 750 | 1,565 | 941 |
| Unprepared source, resolution 64, no Solve | 0 | 745 | 1,563 | 943 |
| Remove all opposed duplicate pairs from no-Solve output | 0 | 0 | 175 | 323 |
| Planar Cap support, resolution 64, Solve | 0 | 0 | 0 | 0 |
| Planar Cap support, resolution 64, no Solve | 0 | 0 | 0 | 0 |

Every duplicate group at resolution 64 is a pair with opposite winding. Removing
these pairs still leaves singular topology. The existing conservative fin
cleanup also fails to produce a valid closed surface. This is not resolved by
ignoring duplicate diagnostics or increasing resolution: unprepared runs at
32, 48, 64, 96 and 128 fail with and without Solve.

## Source boundary and generated candidate

Source-fan extraction finds one continuous 47-edge rim with no singular junction.
It lies on the floor plane: maximum fitted residual approximately 8.04e-12,
with Y approximately 0.0002101. The explicitly selected operation is a disk Cap.

`planar_cap.py` generates 45 constrained triangles, adding no vertices and
preserving every source position and face as an unchanged prefix. It reverses
the patch winding relative to the source boundary. The independent incremental
audit accepts continuity, exact selected-rim coverage, topology and new/old plus
new/new contacts: 520 candidate pair tests, zero improper contacts. The assembled
support has no boundaries, duplicates, degenerate faces, non-manifold edges,
winding conflicts or disconnected fans.

Replaying the same native library with this generated support gives valid
closed-manifold results in **10/10 cases** (32/48/64/96/128, Solve off/on). At
resolution 64 the result has 6,012 faces, compared with 9,160 unprepared faces.
That controlled comparison supports preparing this source's missing floor
before solid voxelization. It does not establish that every open model should
be sealed or that Cap repairs every voxel topology defect.

The old/old source intersections and artistic intent are not globally certified;
the new patch audit deliberately checks only contacts introduced by the patch.
No bake, material, texture, lightmap or Simplify result was tested in this probe.

## Reusable offline profile

The new generator requires a snapshot hash, selected loop ID and explicit disk
intent. It refuses compound/nonplanar rims, source-fan junctions, invalid
projections, stale selections, insufficient budgets and introduced contacts.
It does not classify two rims as disks versus an annular Bridge. Re-analysis
after acceptance retains remaining openings rather than closing them implicitly.

```powershell
python 'Tools~/RemeshCapBenchmark/boundaries.py' --source SOURCE.bin --output BOUNDARY.json
python 'Tools~/RemeshCapBenchmark/planar_cap.py' --source SOURCE.bin --source-hash SOURCE_HASH --loop-id LOOP_ID --intent disk --output SUPPORT.bin
```

The generator uses the benchmark's existing pinned Triangle dependency. The
complete offline suite passes 114 tests, including six box face orientations,
concave rims, retained unselected openings, nonplanar and intersecting refusals,
explicit operation intent, stale selection, cancellation/budgets and determinism.

Private evidence remains ignored under `_results~/sandbag-remesh-20261009/`:
`failure.bin`, decoded stage meshes, `analysis.json`, `cap-audit.json`,
`cap-sweep.json`, `cap-generated.log` and `tests.log`. Neither private capture
geometry nor native binaries are committed.

## Production integration still required

Follow `REMESH_CAP_PLAN.md`: keep synthetic support separate from original
material/lightmap donors, define selected-hole policy, preserve the support
through fitting/Trim, track its provenance and invalidation, and capture original
versus prepared geometry separately. Test Bake/Simplify and incomplete/Bridge
cases before connecting this offline generator to the editor. Existing native
topology guards stay enabled. The live package currently still refuses this
unprepared source.
