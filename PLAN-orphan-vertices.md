# Fix: Orphan Vertices from SymmetrySplit cause Vector3.zero corruption on FBX reimport

## Problem

Pipeline: raw FBX (5158 verts) → MeshOptimizer (5091) → UvEdgeWeld (4613) → SymmetrySplit (+67 = 4680).

`BuildVertexRemap` maps raw FBX vertices → optimized vertices by position+UV0.
SymmetrySplit **creates** 67 new boundary vertices with modified UV0 — they have no raw FBX counterpart.
`ReplayOptimization` iterates the remap table, fills 4613 slots, leaves 67 at `Vector3.zero`.
Result: triangles stretch to origin, Unity reports "inconsistent result" on reimport.

Same problem on all LODs: LOD0 +67, LOD1 +63, LOD2 +41 = 171 orphan vertices total.

## Solution: Store orphan vertex data in sidecar

### Step 1 — `Uv2DataAsset.cs` — Add orphan fields to `MeshUv2Entry`

Add these serialized fields to `MeshUv2Entry`:

```csharp
/// <summary>Indices in the optimized mesh that have no raw FBX source (e.g. SymmetrySplit boundary verts).</summary>
public int[] orphanIndices;
/// <summary>Full vertex attributes for orphan vertices: positions.</summary>
public Vector3[] orphanPositions;
/// <summary>Full vertex attributes for orphan vertices: normals.</summary>
public Vector3[] orphanNormals;
/// <summary>Full vertex attributes for orphan vertices: tangents.</summary>
public Vector4[] orphanTangents;
/// <summary>Full vertex attributes for orphan vertices: UV0.</summary>
public Vector2[] orphanUv0;
```

No other UV channels needed — orphan verts are boundary duplicates, they only differ in UV0.
Colors and bone weights are copied from the vertex they were duplicated from, but we don't track that parent.
Safest: store all per-vertex attributes the optimized mesh has for these indices.

Update `Set()` and constructor to include orphan fields (or better: refactor Set() to accept MeshUv2Entry directly — see REVIEW.md 3.3.4).

### Step 2 — `UvTransferWindow.cs` — Detect orphans in `BuildVertexRemap`

After the existing remap loop, find optimized indices that no raw vertex maps to:

```
After BuildVertexRemap returns remap[]:
  1. Create bool[] covered = new bool[optCount]
  2. For each i in remap: if remap[i] >= 0, covered[remap[i]] = true
  3. Collect all j where !covered[j] → these are orphan indices
  4. Read optimizedMesh vertex attributes at those indices
  5. Store in sidecar.orphanIndices, sidecar.orphanPositions, etc.
  6. Log: "[Apply] '{meshName}': {orphanCount} orphan vertices stored"
```

Location: in `ExecApplyUv2()`, right after `BuildVertexRemap()` call (~line 1683).
The optimizedMesh (`e.originalMesh`) is still alive at this point — read attributes from it.

### Step 3 — `Uv2AssetPostprocessor.cs` — Fill orphans in `ReplayOptimization`

After the existing remap loop that populates `optPos[dst]`, add:

```
// ── Fill orphan vertices (SymmetrySplit boundary, etc.) ──
if (entry.orphanIndices != null && entry.orphanIndices.Length > 0)
{
    for (int k = 0; k < entry.orphanIndices.Length; k++)
    {
        int dst = entry.orphanIndices[k];
        if (dst < 0 || dst >= optCount) continue;
        optPos[dst] = entry.orphanPositions[k];
        if (optNormals != null && entry.orphanNormals != null) optNormals[dst] = entry.orphanNormals[k];
        if (optTangents != null && entry.orphanTangents != null) optTangents[dst] = entry.orphanTangents[k];
        // UV0
        if (optUvs[0] != null && entry.orphanUv0 != null) optUvs[0][dst] = entry.orphanUv0[k];
    }
}
```

Location: right after the `for (int i = 0; i < rawCount; i++)` remap loop (~line 355),
before `mesh.Clear()`.

### Step 4 — Bump version, test

1. Bump `package.json` version (minimum +0.0.1)
2. Test with TrainCarriage FBX:
   - Run full pipeline → Apply
   - Check log for "orphan vertices stored" message
   - Reimport FBX → no "inconsistent result" error
   - Visual check: no vertices snapping to origin
   - Check UV2 in Bakery — lightmap bake should work

## Files to modify

| File | Change |
|------|--------|
| `Editor/Uv2DataAsset.cs` | Add orphan fields to `MeshUv2Entry`; update `Set()` |
| `Editor/UvTransferWindow.cs` | Detect orphans after `BuildVertexRemap`; populate sidecar fields |
| `Editor/Uv2AssetPostprocessor.cs` | Fill orphan slots in `ReplayOptimization` after remap loop |
| `package.json` | Bump version |

## Key constraints

- `MeshUv2Entry` is `[Serializable]` — new fields auto-serialize, old sidecars will have null arrays (safe: null check in replay)
- Orphan arrays are parallel: `orphanIndices[k]` corresponds to `orphanPositions[k]`, etc.
- This is backward-compatible: if orphanIndices is null, replay works as before (just with the existing bug for symmetry-split meshes)
- SymmetrySplit is the only current source of orphan vertices, but this approach handles ANY future pipeline step that adds vertices
