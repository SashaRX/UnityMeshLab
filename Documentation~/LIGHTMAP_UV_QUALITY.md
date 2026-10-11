# Lightmap UV quality in the Viewer

The **UV quality** badge measures the actual displayed meshes and selected UV
channel together. Click it for the component bars and occupied-bbox map. The
calculation is read-only, runs on captured arrays in a cancellable background
worker and refreshes after mesh/cache changes, LOD/channel changes and renderer
transform changes. **Recheck** explicitly refreshes an in-place edit made by an
external tool which did not emit a mesh-change notification.

Keep Quads surface submeshes are triangulated on the captured index arrays,
using the same diagonal as the topology preview. Both displayed meshes and
the LOD0 reference include their quad surfaces; lines and points are excluded.
The original topology, indices and vertex streams remain unchanged.

The score is an approximate diagnostic, not a transfer acceptance gate or proof
that the model will bake correctly. It does not modify the mesh, its source FBX,
the transfer solver or the packer.

| Component | Measurement | Score |
|---|---|---|
| Packing | Union occupancy inside the global axis-aligned UV bbox; largest wholly empty rectangle inside it | `min(100, fill / 0.75 * 100) * exp(-3 * max(0, void - 0.05))` |
| Stretch | Singular-value ratio of the world-surface-to-UV Jacobian; world-area-weighted P95, maximum and area above 4× | `100 * exp(-2 * weighted mean(log(ratio)))` |
| Density | World-area-weighted coefficient of variation of `sqrt(UV area / world area)` | `100 * exp(-2 * CV)` |
| Defects | Missing/nonfinite UVs, zero-area UV/geometry, unexpected positive-area triangle intersections | 100 when none are found, otherwise 0 |
| LOD0 match | Three interior samples per displayed triangle projected onto the same mesh group at LOD0; aligned world-area fraction | 100 times the aligned fraction |

Packing uses four samples per cell on a 256×256 grid, equivalent to a 512×512
sample lattice. Overlapping triangles cover the same samples once. Uniform UV
scale and translation do not change bbox occupancy, stretch or density spread.
A filled rectangle of any aspect ratio can obtain a full packing score; empty
space outside its bbox has no cost. The 75% packing target accommodates ordinary
padding and irregular charts. It is a heuristic, not an exact packing optimum.
Very thin triangles can fall between samples; the map is not a padding audit.

The overall weights are packing 20%, stretch 30%, density 15%, defects 25% and
LOD correspondence 10%. A missing LOD comparison is shown as **n/a** and its
weight is omitted, rather than counted as a successful comparison. An invalid
or missing UV channel scores zero. Collapsed faces, zero-area geometry or any
stretch above 100× cap the score at 39; unexpected overlaps or stretch above
16× cap it at 49; stretch above 4× or an incomplete scan caps it at 69. These
caps keep tiny needles from disappearing behind a good area-weighted mean.
Packing below 40/100 or LOD alignment below 80/100 also cap the overall score
at 79; packing below 20 or LOD alignment below 60 cap it at 59. Density below
40 caps it at 69. Healthy geometry cannot make a sparse/unrelated layout green.
Green starts at 80, yellow at 60, and lower scores are red. The raw quantities
remain visible so that the reason for a low score can be inspected.

LOD samples use a geometric reach of 1% of the donor bounds diagonal and a UV
error threshold of 1% of the donor UV bbox diagonal. Coverage, aligned fraction,
mean and maximum error are reported separately. Different triangle indexing or
triangulation does not cause a failure. New/unmatched groups lower coverage
when at least one group can be measured. An entirely unavailable comparison is
n/a. Sampling is approximate: coincident surfaces with different UVs can make
nearest-surface correspondence ambiguous, and the three samples cannot certify
every point of a triangle. A separately packed LOD will intentionally score
poorly for shared-lightmap correspondence even if its own unwrap is healthy.

Only explicit Reverse UV ancestry permits an intersection: the two faces must
be inherited, in the same chart, have strictly ordered layers, mark the upper
face as intentional detail overlap and name the exact under/over face pair in
the saved relation. A global overlap option alone does not permit a collision.
Wrong source identity, inconsistent face count and missing relations fail
closed. Both allowed and unexpected pair counts are visible. Mirroring an
entire island is legal; a fold with positive intersection area is detected.

Padding in final baked texels, Bakery's final scale/offset, material/shader
behaviour and lighting itself are not assessed. In particular, a clean Reverse
overlap score does not implement ordered baking. The selected UV coordinates
are treated as lightmap coordinates with equal U/V texel size; authored albedo
UVs for a rectangular texture require that texture's aspect correction before
they can be assessed as a square-lightmap layout.

## Verification

`LightmapUvQualityTests` covers rectangular/partial-square layouts, global UV
similarities, internal voids, U/V anisotropy, tiny extreme needles, density
drift, invalid/line UVs, legal seams, ordered versus accidental overlap,
different LOD triangulation, displaced UV correspondence, missing groups,
cancellation/budget exhaustion and Viewer cache/texture lifecycle.

Optional replay environment:

* `MESHLAB_QUALITY_SCORE_FIXTURES`: prior forward/Reverse benchmark output root
  with `Cafe_Table_A_01`, `Modern_Kitchen_A`, `kamaz_Off` binary snapshots and
  `junction/table-improved-{exclusive,overlap}` saved geometry/audits.
* `MESHLAB_QUALITY_SCORE_OUTPUT`: directory for the per-model JSON measurements.

The replay measures eight forward LOD results and six Reverse Table LOD results
under both overlap policies. Reverse pair classification is checked against
the independent saved audit; the input/output snapshots remain unchanged.

The [Kamaz Transfer benchmark](KAMAZ_TRANSFER_BENCHMARK.md) additionally exports
world-space layouts, per-LOD Viewer scores and orthographic stretch/ancestry
contact sheets for the full forward and reverse LOD chains.
