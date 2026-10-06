# Cooler voxel topology investigation — 2026-10-06

Status: reproduced; the solid voxel extraction problem is **not fixed** by 1.1.22.
That version changes only preview orientation. The topology guard still rejects
invalid output before trim and Simplify.

The supplied logs contain a separate request to display the source with zero
scene-root rotation. Preview matrices now remove the root's scene translation and
rotation, preserve scale and child transforms, and retain the captured frame for
generated stages. Export continues using the captured source orientation.

## Reproduction

Imported `Generic_Cooler_01.fbx` and its existing importer settings from the
E-project into the isolated Unity 6000.2.6f2 harness. Captured LOD0 geometry in
root-local space: 408 vertices, 466 triangles. Material reads were disabled for
this geometry-only reproduction; no bake or material interpretation participates
in the failing native voxel extraction.

Source capture SHA-256:
`4f52fa4ded0058d51a4757c541f0c1660accc83b11712f89e6e2f2dd7a6cce30`.

| Resolution | Source fitting | Faces | Duplicate faces | Non-manifold edges | Boundary edges |
|---|---|---:|---:|---:|---:|
| 24 | off/on | 828 | 0 | 0 | 0 |
| 48 | off/on | 3,834 | 2 | 3 | 0 |
| 128 | off | 29,066 | 18 | 37 | 0 |
| 128 | on | 29,066 | 17 | 36 | 0 |
| 256 | off | 116,332 | 141 | 239 | 0 |
| 256 | on | 116,332 | 139 | 237 | 0 |

The resolution-256 results exactly reproduce the user's duplicate/edge counts.
Both have 102 disconnected vertex fans in the managed topology inspection.
Higher resolutions 257–512 also produce invalid topology; raising resolution
alone does not solve this model.

`Bakery: checking for UV overlaps` is a start-of-check message, not an overlap
finding. The failing Mesh Lab invocation aborts before UV generation. Its stack
trace is a managed rejection of invalid voxel geometry, not a native Editor crash.

## Rejected repair experiment

The corner-based polygonizer represents some thin details as coincident,
oppositely wound faces. Removing those pairs and collapsing the remaining short
four-face pinch edges can produce a closed, consistently wound manifold surface.
The fitted resolution-256 candidate removed 278 fin faces and collapsed two edges;
the unfitted candidate removed 282 fin faces and collapsed two edges. Surviving
incident face rotations were bounded to 30 degrees and each moved endpoint to
0.525 voxel. Original arrays were preserved, and all topology gates passed.

However, seven surface probes per changed/removed original triangle (corners,
edge midpoints, centroid) measured deviations up to **2.52 voxels fitted / 2.70
voxels unfitted**. Thus topological validity alone would silently erase thin
details. A 0.525-voxel surface gate rejected this approach; the experimental repair
is not included in the package.

Further extraction work must preserve the thin protrusions while giving them a
valid surface representation. Opposite-face deletion, arbitrary flips and guard
relaxation are insufficient. Validate topology and affected-surface distance on
the same captured input before accepting a replacement.

## Local evidence

`_results~/cooler-voxel/` holds the source, native raw output, resolution sweep,
rejected candidates, distance measurements and experiment code. User model
geometry is kept in local ignored artifacts, not committed to the repository.

Orientation tests cover live Source, all generated stages, relative child
transforms, non-unit root scale, captured frames after scene changes, and the
existing saved-prefab orientation/normalization contract. See
`hierarchy-final.xml` for the final Unity test result.
