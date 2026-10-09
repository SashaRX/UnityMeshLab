# LOD graphical evaluation — 2026-10-09

This evaluation uses actual Unity GPU renders, not reconstructed meshes or generated illustrations. The fixtures and assertions are in [LodVisualQualityTests](../Tests/Editor/LodVisualQualityTests.cs); the comparison-sheet tool is [render_lod_visual_quality.py](../Tools~/render_lod_visual_quality.py).

## Setup and coverage

- Unity 6000.2.6f2, Built-in pipeline, Direct3D12, NVIDIA GeForce GTX 980 Ti.
- Four 6×6 source quad grids (72 triangles): bilinear RGB gradient, a one-row RGBA mask, a sinusoidal surface with amplitude 0.05, and a stronger surface with amplitude 0.18.
- Two fixed orthographic views, frontal and oblique, with 384×384 float readbacks. The preview target itself follows editor DPI; line widths use its actual pixel dimensions.
- LOD1 requests 50% of the source; LOD2 requests 25% (18 triangles). Fast evaluates one strategy and High evaluates five, always from LOD0.
- Target Error 0.05, Normal Weight 0.15, UV2 Weight 0.15, Color Weight 1, Max Normal Angle 15°, Max Color Error 0.03.
- The mask also has an unchecked triangle positive control: native color/normal/UV costs disabled, with no final color guard. This is not an accepted protected LOD.

Each capture contains vertex color, a shaded surface, coverage, alpha visualized as grayscale and a diagnostic wire overlay. Linear RGBA readbacks are retained alongside display-encoded PNGs. The wire overlay alone is displaced 0.002 units toward the camera; the field and shading metrics use untouched geometry.

Pixel statistics compare matching screen pixels inside common coverage, excluding a two-pixel rasterization fringe. Maximum error is the largest absolute RGBA channel difference. RMS averages squared differences over pixels and four channels. Shading RMS uses RGB; silhouette mismatch is the differing coverage divided by union coverage. On curved geometry, screen-space field differences include projected geometric movement, so they are not a pure color-transfer metric. The mesh-space validator separately measures source-surface correspondence.

## Measured LOD2 results

Values below use the worst field maximum/RMS across both views. Actual counts are discrete whole-loop outcomes, not the requested count of 18.

| Fixture | Fast tris | High tris | Fast max RGBA | High max RGBA | Fast RMS RGBA | High RMS RGBA |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Gradient | 12 | 16 | 0.00830 | 0.01111 | 0.00158 | 0.00162 |
| Narrow mask | 16 | 16 | 0.00049 | 0.00049 | 0.00003 | 0.00003 |
| Mild curved surface | 12 | 16 | 0.01416 | 0.01166 | 0.00450 | 0.00254 |
| Strong curved surface | 72 | 72 | 0 | 0 | 0 | 0 |

All protected variants had zero silhouette mismatch in these views. On the mild curved surface, oblique-view shading RMS was 0.00898 for Fast and 0.00978 for High. The unchecked mask control reached 18 triangles but its maximum RGBA error was 0.99707 and RMS was 0.23444: the horizontal band visibly became a distorted diagonal region.

The strong surface retained all 72 triangles because no complete loop passed the current protections/limits. Its zero error proves safe retention, **not successful simplification of curved geometry**. The reduced-fixture assertions require actual reduction for the other three cases, preventing a source-only result from passing their quality checks.

## Assessment

Color protection gives a clear visual benefit on the narrow mask while allowing substantial reduction. RGB gradients also remain close to the source in these small fixtures, though the error maps reveal interpolation changes that are subtle in the ordinary color frame.

High does not provide a universal improvement. It returned the same mask result as Fast, a slightly worse maximum color error on the gradient, and better projected field error on the mild curved surface. It selected 16 rather than 12 triangles on the gradient and mild curve because selection currently prioritizes budget proximity before quality at equal actual count. These results are not comparisons at equal polygon count and cannot establish a backend quality advantage. A future selector should expose that tradeoff rather than treating the High label as a guarantee.

The strong-curvature case shows a practical limitation of conservative complete-loop reduction. Additional strategies cannot help when every operation is blocked by the same hard constraints. A different operator or a validated triangle fallback is needed for such assets.

Four GPU cases passed, with no skips. They also assert visible coverage, actual wire visibility, field/shading limits, and detectable mask loss in the unchecked positive control. Earlier line-preview tests check readback/MSAA, continuous line coverage, near-plane clipping and depth occlusion; they do not by themselves prove LOD quality.

## Reproduction and limits

Run the Editor test fixture `SashaRX.UnityMeshLab.Tests.LodVisualQualityTests` with a graphics device (omit `-nographics`). Set `MESHLAB_LOD_VISUAL_OUTPUT` or pass `-meshlabLodVisualOutput <absolute-output-directory>` to save captures. Then run:

```text
python Tools~/render_lod_visual_quality.py <absolute-output-directory>
```

This produces twelve comparison sheets, per-fixture JSON metrics and `report.md`. The local verified run's XML is `lod-gpu-final.xml`; captures are in the task's visualization folder under `lod-gpu/`. Binary artifacts stay outside the package. C# compile checks run separately from GPU tests.

This section is synthetic, orthographic and limited to two views and one pipeline/resolution. Actual project FBX models are evaluated separately in [Real project LOD evaluation](LOD_PROJECT_VISUAL_EVALUATION.md). Normal-mapped materials, URP parity, perspective distance transitions and categorical region IDs remain unverified. Local timings are single-run observations, not a stable performance benchmark.
