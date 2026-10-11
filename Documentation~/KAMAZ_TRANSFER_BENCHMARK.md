# Kamaz Typhoon transfer benchmark

The optional private fixture compares the complete three-LOD model, including
its doors, pistons and turret stand, in three modes: the ordinary full forward
pipeline, standalone Reverse UV, and standalone Reverse UV with projected
detail overlap. Reverse starts from a normalized geometric LOD2 seed; it does
not consume the forward result. The fixture FBX has mesh compression disabled.
Original inputs are retained; Reverse removes one zero-area LOD1 face on its
working copy and verifies the captured originals after both runs.

## Run and inspect

Use an isolated Unity 6000.2.6f2 consumer with this package in `testables` and the
private fixtures imported. Select these EditMode tests:

```text
PreviewLodSwitchTests.FrozenForwardModelQualityCapture.*kamaz_Off
ReverseUvTransferTests.FrozenKamazFullLodChainAfterCleanup
```

The relevant environment variables are:

| Variable | Value |
|---|---|
| `MESHLAB_FORWARD_QUALITY_OUTPUT` | `<run>/forward` |
| `MESHLAB_FORWARD_DENSITY` | `10` or `32`; default `10` |
| `MESHLAB_REVERSE_OUTPUT` | `<run>/reverse` |
| `MESHLAB_REVERSE_KAMAZ` | directory containing the 61 readonly JSON captures |
| `MESHLAB_REVERSE_KAMAZ_FRAME` | `3` (imported complete LOD chain) |
| `MESHLAB_REVERSE_KAMAZ_REQUIRED_FRAMES` | `3` |
| `MESHLAB_REVERSE_KAMAZ_RESOLUTION` | `512` |
| `MESHLAB_REVERSE_KAMAZ_DENSITY` | same density as forward |
| `MESHLAB_REVERSE_KAMAZ_WORKFLOW` | `1` |
| `MESHLAB_REVERSE_JUNCTION_CUTS` | `1` |
| `MESHLAB_REVERSE_REPACK_FINAL` | `1` |
| `MESHLAB_REVERSE_FILL_FINAL` | `1` for the Viewer's default uniform final fill; `0` for a density-only control |
| `MESHLAB_TRANSFER_BOUNDARY_TEXELS` | `0.25` |

Both modes use two-pixel shell padding. Forward uses adaptive symmetry splitting,
narrow-junction cuts, and automatic atlas resolution. Save the NUnit XML and
Unity log outside the asset tree. The tests export every final mesh as world
positions, UV2 and triangle indices (`*.layout.json`) and save the Viewer's
bbox-relative quality for each LOD (`quality_LOD*.json`). Forward artifacts are
written before quality assertions, so a failed result remains inspectable.
Reverse exports retain the independent audit and its explicit per-face ancestry.

Generate the UV, surface-stretch and overlap-ancestry contact sheets with:

```powershell
python Tools~/render_transfer_comparison.py <run>
```

The script requires NumPy and Matplotlib. It reads saved arrays without changing
them. Surface images are orthographic diagnostics rather than a material or
Bakery bake. Bbox fill counts covered raster samples once even with overlap.
The heatmap uses the saved world geometry; raw quality values come from the same
read-only metric as the Viewer. The ancestry map distinguishes inherited UV,
new UV, and intentional projected detail faces.

## Observations on d5f19ed3, 2026-10-11

- At 10 tex/m, the forward LOD1/LOD2 have 104/91 collapsed UV faces and
  535/96 unexpected intersection pairs. Its existing permissive regression
  gates still pass. The 15 forward mesh snapshots are byte-identical to the
  preceding PR225 review benchmark; artifact export changes no solver output.
- At 32 tex/m, forward LOD1 fails the unchanged mean-stretch gate:
  1494.3384 exceeds 1200. Do not treat this exploratory density run as a pass.
- Reverse's independent audit accepts both overlap modes at both densities:
  zero collapsed UVs and zero unexpected intersections; maximum anisotropy
  remains below 4.001. This is separate from packing and density quality.
- At 10 tex/m, exclusive Reverse splits 178 LOD1 and 468 LOD0 source faces.
  Overlap Reverse rolls the entire refinement trial back on LOD1 face 7920
  (`loses inheritance across a new seam`). At 32 tex/m both modes retain their
  baseline after different inheritance/overlap failures. Zero published split
  faces must not be described as successful seam refinement.
- Final family packing exceeds the 16,000,000-work budget at both densities
  and in both overlap modes. The retained coordinates are valid but sparse.
  Raising density alone does not repair this fallback. The additional 32 tex/m
  run with the Viewer's default uniform final fill enabled also passes both
  independent audits and hits the same packing refusal: all 30 exported layouts
  are byte-identical to the density-only control.
- At 10 tex/m, overlap Reverse retains 1378/3530 independently audited allowed
  intersection pairs at LOD1/LOD0, with inherited world area 85.8%/96.9%.
  These are explicit ancestry relations, not globally waived overlaps.
- The Viewer metric reports one extra LOD0 overlap at 10 tex/m. An independent
  double clipping replay identifies faces 13224/14874: raw intersection area
  is zero; conversion of bbox-normalized coordinates back to Float32 creates
  area 1.785e-16. Its allowed count changes from 3530 to 3528 as well. This is
  a diagnostic precision defect, separate from the saved atlas and raw audit.

The next solver work should isolate seam failures to individual regions while
retaining successful cuts and ancestry, and provide a scalable deterministic
family packer. Forward collapse recovery needs its own Kamaz regression gate;
an area-weighted average or P95 alone hides tiny broken details.
