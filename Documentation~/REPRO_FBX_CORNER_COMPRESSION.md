# Compressed FBX corner mapping: Kamaz_Typhoon

## Observed failure

Unity 6000.2.6f2, package commit `a795d96`, source importer Mesh Compression Low.
`Kamaz_Typhoon_LOD0` failed channel export because its corner tags could not be
decoded. The exporter refused before saving, preserving the original FBX.

The old tag was `(corner, -(meshOrdinal + 1))`. This mesh has 33,744 FBX corners.
The tagged import's maximum integer error was 0.00390625 at Low, Medium and High,
exceeding the old 0.002 guard. The earlier 132-corner metal_beam test did not cover
this range.

## Change

Use `(corner % 4096, -(ordinal * 4096 + corner / 4096 + 1))`. Only the temporary
tagged document receives these values; source UV0 and importer settings stay
unchanged. Each varying component's range is bounded by 4095, independently of
the full corner count. Decode only finite values within 0.125 of an integer,
validate the low digit and ordinal, and reject a corner outside the source mesh's
corner count. Allocate the corner table using that source count, including
corners discarded by the importer.

The format supports 4096 meshes and fewer than 2^24 corners per mesh. Invalid
tags still refuse the whole write; this does not bypass face-matching guards.

## Verification, 2026-10-09

Actual Unity EditMode run: 102/102 FBX channel, structure and export tests passed.
The new regression uses a 96x96 quad grid (36,864 corners), checks every written
UV1 corner, exact source UV0, control points, polygon loops, source bytes and
importer metadata. It covers compression Off/Low/Medium/High and triangulated
High import. Both reference-assembly C# builds also passed.

On a copy of the user's Kamaz FBX with all 15 LOD meshes loaded, channel export
passed at Off, Low and Medium. All 63,658 LOD corners received the expected
UV1 values; source UV0, control points, polygon loops, source bytes and importer
metadata were preserved. The live FBX and metadata hashes remained unchanged.
The real-asset probe used a different constant UV1 value per mesh; the grid
regression checks corner-specific values.

At High, the same asset is still safely refused: one body LOD0 corner has no
matching face in the working mesh. This is a separate face-correspondence
failure, not a tag-decoding failure, and was not bypassed by this fix.

Evidence is local and ignored: `_results~/kamaz-fbx-20261009/` (`tags.log`,
`write-full.log`, `fbx-tests.xml`, `compile-final.log`). Original FBX SHA256:
`A157DCF543D60A66BD9FCF07EBAE96B56EEFCF14F52E18385FC3E622085BD48B`.

## Transfer warning remains separate

The supplied log also reports 30 collapsed/sliver target shells. This change
only fixes corner transport during export; it does not repair those UVs.
A current-state capture from the live project completed, but contained no
repacked/transferred meshes or historical pairs. It cannot reproduce that run;
arm Capture Next Transfer Run before repeating the pipeline to retain its exact
inputs, settings, hints and outputs.
