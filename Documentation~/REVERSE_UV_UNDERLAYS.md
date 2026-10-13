# Reverse UV: underlays, seams and family packing

Continuation of the progressive Reverse UV experiment on PR225. Baseline:
`eccca7d1439c7701b5a03790a763f3b140896b1b`. This is a separate coarse-to-fine
transfer workflow; a preceding ordinary forward Transfer is not required.

## Problems reproduced

1. A newly introduced fine-LOD base and its raised detail were both independently
   unwrapped. Only the previous LOD was searched for a donor, so enabling overlap
   did not put the detail on its newly introduced underlay.
2. A raised detail sharing a rim with its underlay was rejected as though it
   were a coplanar duplicate. The two new overlap controls failed on the baseline.
3. Existing-edge junction paths depended on tessellation. Increasing the chart
   count did not prove that E/T/U/O/P/F/H/L/X/V/C had become compact pieces.
4. Reserving an entire family rectangle wasted holes and gaps. It prevented a
   small unrelated family from occupying the interior of a padded ring.
5. The initial footprint search performed expensive geometry validation for
   placements that could not improve its selected extent. The first complete
   corpus attempt was stopped; its partial results are not a full test pass.
6. Applying flat-frame cuts to a curved Tire_C chart reduced inherited coverage.
   These cuts now require a coherent nearly planar surface (1% span deviation,
   normal agreement of 0.99). Curved chart parameterization is a separate task.
   A clamped closest point on a neighbouring face is also excluded from same-LOD
   underlay projection: its lateral offset must fit 1% of projection depth.

## Required invariants

- Geometry chooses the underlay; renderer order does not choose the layer.
- A projected corner takes its underlay's barycentrically interpolated UV.
  It is not assigned a nearby free packing cell. Interior and complete footprint
  checks reject projection across holes or unrelated charts.
- Newly introduced underlays participate in the current LOD's correspondence
  graph. Their support flag is persisted in the audit and Viewer provenance.
- Coplanar independent renderers are not invented as detail/underlay layers.
- Every inherited family, its descendants and its explicit overlays receive
  one rigid transform and one common scale during final packing.
- A retained region/chart number is not itself a packing family. Independent
  placements of disconnected region pieces must first be separated by actual
  geometric/UV corner connectivity within each surface. Explicit donor and
  ordered-overlay edges then join those components across the LOD chain.
- Only existing ordered overlap relations may survive packing. Intentional
  overlap must remain at its underlay coordinates, not merely somewhere inside
  the underlay's bounding box.
- A rejected optional projection restores its allocated independent UVs.
  Descendants of a restored underlay are restored too; unrelated accepted
  projections remain available.
- Source FBX and input mesh streams remain unchanged. New corners interpolate
  attributes on a detached copy and retain original face/barycentric provenance.

## Implementation sequence

1. Remove exactly zero-area faces on detached inputs. Normalize and repair the
   coarsest LOD in the world metric, independently of UV0 texture aspect.
2. Before this atlas has descendants, extend candidate seams from reflex UV
   boundary corners through triangle interiors of nearly planar charts. Clip and triangulate locally;
   retain only area-complete subdivisions with a smaller sum of oriented boxes.
   Seams stop at the chosen boundary hit rather than continuing across unrelated
   areas of the island. Nearly coincident Float32 lines are deduplicated instead
   of producing tiny extra faces. A prospective seam that introduces a triangle
   below the final-coordinate precision reserve is skipped locally. Adjacent
   pieces may rejoin when their union is a compact rectangle.
3. Rotate new strip charts to their compact axis when rotation is enabled.
   Inherited charts are not independently rotated or recut by this preparation.
4. Project the next finer LOD, splitting target triangles at donor seams. Newly
   unsupported geometry gets the same strip preparation before chart allocation.
   Compose barycentric maps through both kinds of subdivision to the original
   pre-cleanup source faces.
5. In overlap mode, resolve new detail against current-LOD lower surfaces as well
   as the previous donor. Evaluate lower dependencies before interpolating UVs;
   check normal compatibility, ambiguity, stretch, winding, interior mapping and
   complete parent-layer coverage. Refused detail retains an independent chart.
6. Record actual same-LOD overlap edges. Form shared chart families for the whole
   chain, then pack the union of their triangle footprints. Conservatively dilated
   row masks protect gutters; empty regions inside a family can accept another
   family. All descendant and overlay coordinates move together.
7. Validate actual exported Float32 coordinates and source partitions. The
   existing metric, contact and cancellation gates remain active. Optional uniform
   final filling records its resulting density.

The final rigid-family placement uses the same source-local double-precision
metric as the export audit and Viewer. Both pre-move and post-move altitudes
below the configured texel boundary tolerance allow Float32 relative metric
drift; the absolute stretch limit, positive-area and overlap checks still
apply. Resolved triangles retain the 1% no-regression derivative gate. This
policy does not authorize a new overlap or an independent detail placement.

Alternative footprint orders have independent work and validation budgets;
preliminary tightening cannot consume their budgets before they start. Search
diagnostics record moved families, completed orders and exhausted work rather
than presenting a retained layout as successful footprint compaction.

The footprint pass currently improves an already valid rectangle placement at
the selected atlas size. It is not a proof of globally optimal packing. Projection
still requires the configured reach and normal agreement; unrelated or unsupported
detail is deliberately allocated separately. Flat-frame cuts are not applied to
strongly curved charts. General curved-chart distortion and
ordinary forward-transfer quality remain separately measured problems.

## Research informing this design

- [xatlas implementation](https://github.com/jpcy/xatlas): chart construction and
  packing are separate stages; the packer rasterizes chart triangles and expands
  occupancy for filtering and padding. Independently submitting disconnected LOD
  pieces as UV meshes would not preserve a shared family as one packing item, so
  this pass rasterizes the complete family union. No external implementation was
  copied into the package.
- [Texture Mapping Progressive Meshes](https://hhoppe.com/proj/tmpm/): stable
  correspondence across resolutions matters alongside stretch. A dense LOD0
  atlas alone does not demonstrate a valid shared lightmap parameterization.
- [Texture Transfer During Shape Transformation](https://faculty.cc.gatech.edu/~turk/my_papers/texture_transfer.pdf):
  target triangles crossing texture seams need subdivision and barycentric
  transfer of the new vertices. Interpolating only existing target vertices
  cannot express a discontinuous donor chart.

## Verification artifacts

Private model inputs remain outside the package. Replays and numeric reports
are under `_results~/reverse-family-packing-20261011/` in the working checkout.

- `overlap-baseline-fail`: the two new baseline failures (attached rim; new
  same-LOD underlay), alongside the existing suite.
- `compact-strip-controls`: 45 passing EditMode controls. Tests check each piece's
  compactness, complete source area, interpolated attributes, original input
  bytes, both overlap policies, renderer-order reversal, stacked parents and
  rigid family packing. Sparse T controls use three scales, rotation and reflection.
- `compact-strip-controls/strips-comparison.png`: actual Unity output before
  cuts, after cuts and after packing; used to catch unnecessary fragmentation and
  diagonal strips rather than judging chart count alone.
- `cafe-subdivision`: four audited full Cafe chains (seam splitting on/off,
  overlap on/off). The improved overlap trial retains all audited inherited area.
  This run precedes the final axis-alignment and seam-deduplication corrections.
- Full corpus, forward snapshots and final Kamaz results are recorded in the
  experiment log after completion; partial or cancelled runs do not count.

Latest private comparisons and remaining model failures are recorded in
[FORWARD_REVERSE_COMPARISON.md](FORWARD_REVERSE_COMPARISON.md), current continuation
section. Passing the analytic controls or inherited-area corpus gate does not
establish acceptable Kamaz exclusive packing or complete overlay correspondence.
