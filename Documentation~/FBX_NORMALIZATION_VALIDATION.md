# Static FBX normalization

## Required result

Preserve the mesh's physical size and orientation as shown in Unity, place the
original root pivot at the origin, and bake rotation and scale into geometry.
FBX nodes have translation `(0,0,0)`, rotation `(0,0,0)` and scale `(1,1,1)`.
In 3ds Max the last value is displayed as `(100,100,100)%`.

Normalized files use meters and the FBX SDK's Max Z-up axis system. Use **meters
for Max System Units**, matching the supplied `uvbest.fbx` (unit factor 100 cm).
Display Units alone do not control FBX unit conversion. A Max scene using another
system unit can still introduce an import conversion scale.

## Changes

- Remesh weld saving bakes the complete captured linear matrix, including rotation
  and parent-induced shear. Normalized prefab roots have identity transforms.
- General hierarchy normalization captures accumulated matrices before resetting
  any ancestor. Root and nested mesh transforms are baked into separate copies;
  shared source mesh buffers remain untouched. The root pivot is retained.
- Normalized FBX coordinates are converted from centimeters to meters as well as
  changing unit metadata. Axis conversion includes normals and tangents. The
  export copy compensates Unity Exporter/importer handedness conventions so
  reimport does not introduce a 180-degree rotation.
- Normalized Unity import uses file units, global scale 1 and baked axis conversion.
  Narrow channel exports retain their existing behavior. Embedded texture data
  survives the SDK rewrite. Failed atomic conversion leaves target bytes and meta intact.
- Skinned hierarchy normalization is rejected before changing clone transforms;
  bone bind poses need a different normalization procedure.

## Verified locally, 2026-10-05

Unity **6000.2.6f2**, D3D11, Unity FBX Exporter **5.1.5**, Autodesk SDK package **5.1.1**:

- **41/41 EditMode tests passed**, including both direct and atomic actual FBX writes.
- Reopened FBX has meter units, Max axes, identity local transforms and zero pre/post rotation.
- Reimported asymmetric, translated and rotated geometry preserves bounds, normals,
  tangents and bitangents. Remesh saves preserve captured size/orientation after the
  source transform changes; source preview stages remain unchanged.
- Root scale, nested rotations/nonuniform scales, mirrored winding, shared mesh copies,
  embedded texture extraction without the original map and failed overwrite rollback tested.
- License-free C# builds pass with and without the FBX define.

Scratch evidence: `_results~/fbx-normalization/final-tests.xml`, `write.fbx`,
`atomic.fbx`, `serialized-fbx.json`; compile evidence in `_results~/fbx-normalized-compile/`.
These local artifacts are ignored and are not shipped in the UPM package.

Direct 3ds Max 2024 batch validation was attempted in a separate process using an
isolated INI. Startup stalled before running the validation script; those processes
were stopped. No claim of successful live Max import is made. The user's exact
problematic FBX and current Max System Units were requested but were not available
at validation time.
