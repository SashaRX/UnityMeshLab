# Normal padding and merged tangent frames (2026-10-05)

The reported pale lines disappeared when the baked normal map was disabled.
The retained bust replay reproduced them with the original tangents and removed
the prominent cheek, eye and chin lines after rebuilding the final tangent frames.

## Causes and changes

- The merged tangent rebuild accumulated area-weighted vectors in mesh units,
  then rejected vectors below an absolute squared-length threshold. At the
  bust's approximately 0.0053 m diagonal it retained pre-merge tangent directions
  even though UVs had changed. Edges are now scaled before area accumulation;
  normalization tests direction validity rather than world-unit magnitude.
- Removed UV cuts retained separate vertex indices. Matching final position,
  normal, UV and chart copies now share their accumulated tangents and bitangents.
  Different charts and different shading normals retain independent frames.
- Geometric unfolding still locates surface samples across trusted physical
  neighbors. Normal transport uses shading discontinuities at their shared edges:
  a continuous shading frame no longer acquires an extra geometric-frame twist.
- The receiver's affine shading frame continues beyond its UV boundary, including
  the dilation margin. A singular or reversed continuation uses its closest
  reachable boundary frame. Surface sample positions still follow the connected
  3D mesh; the extended frame does not change projection positions.

## Verification

Unity 6000.2.6f2, URP 17.2, DX11, Android build target, DXT5nm encoding; isolated
validation project. The source retained its imported orientation and materials.
Source and result renders used identical camera, lighting and display transforms.
No source geometry, simplification settings, UV settings, normal strength or
padding radius was changed to improve the comparison.

Capture SHA256: `8dc394ed0c40c4292b1056c18de75533e78281a9d82b8d1f1a20d7501b2514db`.
Result: 1360 vertices, 1598 triangles, 47 charts; texture 1024, padding 3,
dilation 64, 16 samples per texel. Positions, normals, indices, UVs and chart IDs
were byte-identical before/after. The base-color PNG was also byte-identical.

| Matching final seam copies | Before | After |
|---|---:|---:|
| Compared pairs | 121 | 121 |
| Mean tangent angle | 92.426 degrees | less than 0.000002 degrees |
| Maximum tangent angle | 178.747 degrees | less than 0.000002 degrees |
| Pairs above 1 degree | 116 | 0 |

Regression controls failed before their corresponding fixes: four smooth-frame
transport cases, two bilinear boundary-frame cases, and tangent rebuilds at
0.002 and 0.000001 mesh scales. Final checks cover bake/projection/padding,
Built-in and URP frames, merged tangent rebuilding, mirrored UVs and preservation
of real UV/normal discontinuities. Licence-free C# builds pass with and without
the FBX exporter define.

Retained local artifacts under `_results~/normal-seam-fix/`:

- `validated-comparison.json`: array equality, tangent-copy angles and correct
  before/after render paths.
- `bust-frame/cpu-source-normal/close-before-yaw215.png`: original map rendered
  with the original tangent basis.
- `bust-tangents/cpu-source-normal/close-after-yaw215.png`: freshly baked map
  rendered with the corrected tangent basis.
- `bust-tangents/result.json`: final source-normal and geometry-only CPU bake
  reports; `verified-tests.xml`: final Unity test results.

The `close-before` images and `before` seam metrics inside the final
`bust-tangents` directory deliberately combine the old map with the new tangent
basis. They are incompatible-basis diagnostics, **not** a valid before render;
use the paths in `validated-comparison.json` for comparison.

Experiments that encoded each sample independently or optimized filtered edge
agreement did not remove the prominent lines and were not included in production.
This validation addresses the retained bust and these regressions. It does not
establish that all projection folds, negative tangent-Z samples or all graphics
API/device rendering issues have been resolved. After upgrading, rerun **Unwrap**
to rebuild tangents, followed by **Bake**; existing maps encode the old basis.
