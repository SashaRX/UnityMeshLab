# Prepare: component surface binding

The Colliders step can attach a game's component and assign one existing surface
asset to its serialized object-reference field. The package has no reference to a
game assembly. It does not create surface assets, set their physics/damage values,
or assign `Collider.sharedMaterial`.

## Project configuration

Open **Project Settings > Mesh Lab > Prepare > Collider Surfaces**.

| Setting | Value to obtain from the game |
| --- | --- |
| Component | Fully qualified, concrete runtime component type |
| Field | Serialized field name, including any leading underscore; not the C# property or setter |
| Asset Type | Fully qualified asset type assignable to that field |
| Surface words | Keyword to exact asset object name, without `.asset` |

The component's field may be public or private with `[SerializeField]`. The
binding writes it through `SerializedObject`; it does not invoke a custom setter.
Check whether the game's setter has necessary side effects before using this
binding. Prefer full type names: short names can collide across assemblies.

Enable **Colliders** and **Surfaces** in the matching recipe. For FBX hierarchy
changes also enable **Prefab**, which writes a variant. The first matching recipe
wins; the default only matches `SM_*`. **Run on Import** is opt-in and handles
first imports without an existing `.meta`, not ordinary reimports.

## Recognition and asset selection

1. First surface word in the part's name.
2. First recognized material name: the node's own materials, followed by matching
   render siblings for a collision node. Siblings share `MeshNaming.GroupKey`
   after LOD/collision suffix removal.
3. First surface word in the target model's name.

Surface words are case-insensitive prefixes of individual name tokens. Tokens
split at separators, case boundaries and digits. `wood` matches `Wooden`, but a
rule containing `reinforced_concrete` cannot match two tokens. Use a single
unambiguous token for a special surface and map it to the actual asset name.

The defaults are wood, concrete, metal, glass, plastic, tile and cardboard. A
game may need different names and additional rules. The binder does not inspect
a surface enum inside the asset. It searches assets of the configured type and
requires an exact object-name match ignoring case. Duplicate names select the
longest shared folder prefix with the target, then the ordinal path. A scene
target uses its scene path, so duplicate names can resolve differently from a
model target. Keep semantic asset names unique where possible.

## Boundaries to check before a batch

| Case | Current behavior / consequence |
| --- | --- |
| One node with Wood and Metal render materials | First recognized material wins for the entire collider; no submesh/triangle assignment, conflict warning or automatic split |
| Existing non-null reference on the destination object | Preserved even if naming suggests another surface; no asset lookup |
| Missing asset or unrecognized surface | Warning; no surface component is added; collider preparation can still succeed |
| Valid repeated Apply | Existing binding is kept and no duplicate component is added |
| Root already has any MeshCollider | Whole Colliders step is skipped, including all surface bindings below it |
| Asset becomes available after a previous failed binding | A non-root collider with recognizable naming can retry; a root MeshCollider prevents the retry |
| Largest collision node moves to root | Only the destination object's existing binding is checked; a custom binding on the source node is not transferred |
| Several colliders on one GameObject | One component/reference is shared by that object; not a per-collider mapping |

For mixed surfaces, author separate explicitly named collision parts, e.g.
`Seat_Wood_COL` and `Frame_Metal_COL`. Check any largest-part relocation when
manual surface components already exist. Do not remove an intentional root
collider merely to force Prepare to run. Existing root-collider models need a
separate migration/fill step, which this API does not currently provide.

A collision node whose material name is its only surface evidence loses that
evidence when its renderer is removed. A successful binding survives, but a
missing asset should be fixed before Apply; explicit surface names also allow a
later retry on non-root colliders.

## Verification

`PrepareCollidersTests` covers own-material Preview/Apply parity, explicit name
priority, matching render fallback, preservation of a different manual reference,
missing-asset retry on a non-root collider, repeated Apply, and incompatible types.
`PreparePrefabTests.ComponentBinding_UsesNearestExactAssetAndPersistsAcrossPrefabReload`
uses real temporary assets and reloads a prefab variant to check persistence.

Run these in Unity's EditMode Test Runner. A license-free compile check verifies
C# types but does not execute Unity serialization, Undo or AssetDatabase behavior.
