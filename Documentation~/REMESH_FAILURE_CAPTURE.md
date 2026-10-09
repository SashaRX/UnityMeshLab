# Remesh failure geometry capture

Failure paths must preserve the geometry that a topology guard rejected. The
old Verbose-only capture occurs after `Voxelize` and Trim, so it cannot record a
solid preflight exception. Simplify input failures also need the exact upstream
input, not only the call stack.

## Captured failures

- Solid voxel rejection without Solve: original source and rejected raw result.
- Failed fallback without source fitting: original source, first raw attempt and
  rejected retry, retaining both actual native flag sets and resolution.
- `InvalidOperationException` during pipeline Simplify: original source, the
  retained raw voxel when available, exact Simplify input and current request
  settings. Cancellation is not captured as a geometry failure. For this stage,
  flag/resolution fields describe the current request; if settings changed after
  Remesh they do not reconstruct the earlier native attempt.

Voxel and Hull callers supply the captured node name. The public `Voxelize` API
is unchanged; standalone callers have no node label. Hull captures the effective
coarse settings passed to native, including Hull resolution, Solve=false and
Shell=false. The failure filename is logged in the General warning category so
it does not require the RemeshDiag category. File writing itself is independent
of log level/mask; a muted logger can suppress the path message.

The guards, retries and cancellation behavior are unchanged. Capture is not a
repair and never authorizes invalid geometry downstream. Failed capture I/O is
reported and does not replace the original topology exception.

## Files and decoder

Captures live under `%TEMP%/meshlab-uvmerge/failures/`. A completed capture is
published by moving a temporary file after closing the writer. The writer keeps
the last three failure captures, separate from the old successful-stage dumps.
Concurrent writers are serialized within the editor domain. Private captures
must not be committed.

Binary layout is little endian:

1. int32 magic `0x524D4C42`, int32 version **2**.
2. BinaryWriter UTF-8 string with a 7-bit byte-length prefix, containing JSON:
   stage, node, reason, settingsJson, resolution, initialFlags and resultFlags.
3. Three named-in-order slots: **source**, **raw**, **input**. Each starts with
   a Boolean presence byte. A present slot contains int32 vertex/index counts,
   float32 XYZ positions and int32 triangle indices. An absent slot has no mesh
   bytes and must not be interpreted as a copy of another stage.

The prior successful-stage version 1 format remains unchanged. Its reader must
not treat the new version as a version 1 payload.

```powershell
python 'Tools~/RemeshCapBenchmark/failure_capture.py' --capture FAILURE.bin --output PRIVATE_OUTPUT
```

The decoder validates version, metadata, completeness, triangle index bounds,
finite positions and trailing data before exporting. Use a new or empty output
directory; existing files are refused to avoid mixing stages from different
captures. It creates `capture.json`,
plus `.bin` and `.npz` for each present slot. `.bin` uses the simple source/Cap
benchmark layout; NPZ contains non-pickled `positions` and `indices`. Topology
summaries weld geometric positions. No Cap-prefix assumption is made about raw
or Simplify input. These reports do not test 3D intersections or UV quality.

## Cases motivating the change

User logs from Unity 6000.2.6f2 on 2026-10-09:

| Model / path | Installed package | Rejection | Duplicate faces | Non-manifold edges | Disconnected fans |
| --- | --- | --- | ---: | ---: | ---: |
| SandbagWall_D / Hull | 313fc3a | solid before Trim | 2,876 | 6,475 | 3,995 |
| SandbagWall_D / Voxel | 313fc3a | solid before Trim | 13,134 | 29,556 | 17,771 |
| CourtyardFacilitiesSet_TrashCan / Voxel | d9a0e2b | solid before Trim | 302 | 511 | 258 |
| CourtyardFacilitiesSet_TrashCan / Hull | d9a0e2b | solid before Trim | 302 | 511 | 258 |
| Earlier unidentified Simplify input | d9a0e2b | before simplification | 21 | 100 | 85 |

The solid cases have zero boundary and degenerate faces. The earlier Simplify
input has 44 boundary edges and zero degenerates; open boundaries alone are
allowed by Simplify. Bakery lines only announce UV overlap checks. These logs
contain neither geometry nor effective resolutions, so equal counts do not
prove identical meshes or settings. The error does not establish that the
source FBX is at fault; capture the next attempt to isolate source, native and
Trim contributions. Experimental compound Cap remains outside production.

## Verification

Real Unity EditMode tests cover fatal solid observation with and without retry,
both rejected meshes and final flags, successful fallback/valid solid/Shell
exclusion, cancellation, binary round-trip, optional missing raw, retention,
unrelated-file preservation and unchanged input arrays. Python decoder tests
cover Unicode BinaryWriter metadata, exact float/index export, absent slots,
duplicate topology, truncation, old-version refusal and invalid index refusal.
Reference C# compilation is checked with and without the FBX exporter define.
