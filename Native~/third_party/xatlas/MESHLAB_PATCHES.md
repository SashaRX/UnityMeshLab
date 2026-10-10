# Reviewed Mesh Lab extension

The pinned xatlas implementation has one local extension: `PackOptions.preserveChartShape`
(default false). Native Remesh unwrap and its managed repair pack opt into it.
The existing `xatlasPackCharts` export retains its behavior for UV Transfer;
the additional `xatlasPackChartsPreserveShape` export is used by Remesh only.

Upstream ceil-rounds each chart's width and height and stretches UV coordinates
independently to fill that pixel rectangle. Subpixel chart dimensions can cause
large anisotropy and destroy consistent area density even after parameterization.
The extension retains the uniformly scaled UV coordinates and uses the rounded
rectangle only for raster allocation and placement. Chart images and padding are
generated from the retained shape, before packing; no post-pack UV correction
can introduce untested overlaps.

This adds no field to the exported C ABI or serialized managed settings. Other
xatlas users retain the upstream behavior unless they explicitly opt into it.
Keep this patch when updating the pinned vendor snapshot; native CI and the UV
shape regressions must pass before publishing Plugins.
