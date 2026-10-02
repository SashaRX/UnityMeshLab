// SidecarStore.cs — the one reader and writer of the `_uv2data.asset` sidecar that
// travels next to an FBX: UV2 entries, collision hulls and the transfer tool's
// settings. Tools used to open the asset themselves (load-or-create, Set, SetDirty,
// SaveAssets) in six places and to collect "the FBX paths behind these entries" in
// five; every one of those goes through here now.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Persistence of <see cref="Uv2DataAsset"/> sidecars. Nothing here touches the
    /// scene or an importer; the postprocessor's replay sets are armed only by
    /// <see cref="ArmTransientReplay"/>, which the export calls when sidecar mode is off.
    /// </summary>
    internal static class SidecarStore
    {
        /// <summary>`Assets/Models/Chair.fbx` → `Assets/Models/Chair_uv2data.asset`.</summary>
        internal static string PathFor(string fbxPath) => Uv2DataAsset.GetSidecarPath(fbxPath);

        /// <summary>The sidecar next to <paramref name="fbxPath"/>, or null when there is none.</summary>
        internal static Uv2DataAsset Load(string fbxPath)
        {
            if (string.IsNullOrEmpty(fbxPath)) return null;
            return AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(PathFor(fbxPath));
        }

        /// <summary>The sidecar next to <paramref name="fbxPath"/>, created on disk when there is none.</summary>
        internal static Uv2DataAsset LoadOrCreate(string fbxPath)
        {
            string sidecarPath = PathFor(fbxPath);
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<Uv2DataAsset>();
                AssetDatabase.CreateAsset(data, sidecarPath);
            }
            return data;
        }

        /// <summary>True when a sidecar exists next to <paramref name="fbxPath"/>.</summary>
        internal static bool Exists(string fbxPath) => Load(fbxPath) != null;

        /// <summary>Marks the sidecar dirty and writes every pending asset change.</summary>
        internal static void Save(Uv2DataAsset data)
        {
            if (data == null) return;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        // ── FBX paths behind a selection ──

        /// <summary>
        /// The distinct `.fbx` asset paths the entries were imported from (the FBX
        /// sub-asset first, the working mesh when there is none), in entry order.
        /// </summary>
        internal static List<string> FbxPaths(IEnumerable<MeshEntry> entries)
        {
            var paths = new List<string>();
            if (entries == null) return paths;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e == null) continue;
                Mesh m = e.fbxMesh ?? e.originalMesh;
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (IsFbxPath(p) && seen.Add(p)) paths.Add(p);
            }
            return paths;
        }

        /// <summary>The `.fbx` asset paths under the meshes of <paramref name="root"/>'s MeshFilters.</summary>
        internal static List<string> FbxPaths(GameObject root)
        {
            var paths = new List<string>();
            if (root == null) return paths;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                string p = AssetDatabase.GetAssetPath(mf.sharedMesh);
                if (IsFbxPath(p) && seen.Add(p)) paths.Add(p);
            }
            return paths;
        }

        /// <summary>True for a non-empty path ending in `.fbx` (any case).</summary>
        internal static bool IsFbxPath(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The first of <paramref name="fbxPaths"/> that has a sidecar, with that sidecar's
        /// path; false when none has one.
        /// </summary>
        internal static bool TryFindFirst(IEnumerable<string> fbxPaths, out string fbxPath, out string sidecarPath)
        {
            fbxPath = sidecarPath = null;
            if (fbxPaths == null) return false;
            foreach (string fbx in fbxPaths)
            {
                string sp = PathFor(fbx);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) == null) continue;
                fbxPath = fbx;
                sidecarPath = sp;
                return true;
            }
            return false;
        }

        // ── UV2 entries ──

        /// <summary>
        /// Adds or replaces <paramref name="entries"/> in the sidecar next to
        /// <paramref name="fbxPath"/>, creating it when needed. Returns the number stored.
        /// </summary>
        internal static int SaveEntries(string fbxPath, IEnumerable<MeshUv2Entry> entries)
        {
            if (string.IsNullOrEmpty(fbxPath) || entries == null) return 0;
            var data = LoadOrCreate(fbxPath);
            int saved = 0;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                data.Set(entry);
                saved++;
            }
            if (saved > 0) Save(data);
            return saved;
        }

        /// <summary>
        /// Drops the UV2 entries of every sidecar behind <paramref name="fbxPaths"/>.
        /// Collision entries are kept; a sidecar left with nothing is deleted.
        /// Returns the number of sidecars that had UV2 entries.
        /// </summary>
        internal static int ClearUv2Entries(IEnumerable<string> fbxPaths)
        {
            int cleared = 0;
            if (fbxPaths == null) return 0;
            foreach (var fbxPath in fbxPaths)
            {
                if (string.IsNullOrEmpty(fbxPath)) continue;
                string sidecarPath = PathFor(fbxPath);
                var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
                if (data == null) continue;

                bool hasCollision = data.collisionEntries != null && data.collisionEntries.Count > 0;
                bool hasUv2 = data.entries != null && data.entries.Count > 0;
                if (!hasUv2) continue;

                data.entries.Clear();
                cleared++;
                if (hasCollision) EditorUtility.SetDirty(data);
                else AssetDatabase.DeleteAsset(sidecarPath);
            }
            if (cleared > 0) AssetDatabase.SaveAssets();
            return cleared;
        }

        /// <summary>Deletes the sidecars behind <paramref name="fbxPaths"/>. Returns how many existed.</summary>
        internal static int Delete(IEnumerable<string> fbxPaths)
        {
            int deleted = 0;
            if (fbxPaths == null) return 0;
            foreach (string fbx in fbxPaths)
            {
                if (string.IsNullOrEmpty(fbx)) continue;
                string sp = PathFor(fbx);
                if (AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sp) == null) continue;
                AssetDatabase.DeleteAsset(sp);
                deleted++;
            }
            return deleted;
        }

        /// <summary>
        /// Hands <paramref name="entries"/> to the postprocessor for the next import of
        /// <paramref name="fbxPath"/> only (sidecar mode off): the UV2 is replayed once and
        /// nothing is written to disk.
        /// </summary>
        internal static void ArmTransientReplay(string fbxPath, List<MeshUv2Entry> entries)
        {
            if (string.IsNullOrEmpty(fbxPath) || entries == null || entries.Count == 0) return;
            Uv2AssetPostprocessor.SetTransientReplayEntries(fbxPath, entries);
            Uv2AssetPostprocessor.managedImportPaths.Add(fbxPath);
            Uv2AssetPostprocessor.transientReplayPaths.Add(fbxPath);
        }

        /// <summary>
        /// Stores <paramref name="entries"/> for <paramref name="fbxPath"/> the way the
        /// current sidecar mode wants: on disk when it is on, as a one-shot replay when
        /// it is off. Either way the postprocessor treats the next import as managed.
        /// </summary>
        internal static void StoreForImport(string fbxPath, List<MeshUv2Entry> entries, bool persistent)
        {
            if (persistent) SaveEntries(fbxPath, entries);
            else Uv2AssetPostprocessor.SetTransientReplayEntries(fbxPath, entries);
            Uv2AssetPostprocessor.managedImportPaths.Add(fbxPath);
            if (!persistent) Uv2AssetPostprocessor.transientReplayPaths.Add(fbxPath);
        }

        // ── Entry construction ──

        /// <summary>
        /// Which UV component vertex AO was written to, as the export needs it: a UV
        /// channel (0–7) and component (0 = X, 1 = Y). <see cref="None"/> when AO went to a
        /// vertex colour channel, nothing was applied, or the channel is UV1 — the lightmap
        /// channel, which the export never merges AO into.
        /// </summary>
        internal readonly struct AoUvTarget
        {
            public readonly int channel;
            public readonly int component;
            public AoUvTarget(int channel, int component) { this.channel = channel; this.component = component; }
            public bool IsSet => channel >= 0;
            public static readonly AoUvTarget None = new AoUvTarget(-1, 0);

            /// <summary>The target behind a vertex-AO channel choice, honouring the UV1 rule.</summary>
            public static AoUvTarget From(AOTargetChannel? applied)
            {
                if (!applied.HasValue) return None;
                int channel = VertexChannels.UvChannel(applied.Value);
                if (channel < 0 || channel == 1) return None;
                return new AoUvTarget(channel, VertexChannels.UvComponent(applied.Value));
            }
        }

        /// <summary>
        /// The UV2 sidecar entry for one exported mesh: the result's UV1 (or the AO
        /// channel when UV1 is absent), positions, UV0, colours, the source fingerprint
        /// and the pipeline steps the entry went through. False when the result has no
        /// UV1 and no AO channel to store.
        /// </summary>
        /// <param name="isSourceLod">True when the entry is the repack (source) LOD, false for a transfer target.</param>
        internal static bool TryBuildEntry(MeshEntry entry, Mesh resultMesh, bool isSourceLod, AoUvTarget ao, out MeshUv2Entry sidecarEntry)
        {
            sidecarEntry = null;
            if (entry == null || resultMesh == null) return false;

            var sidecarMesh = UnityEngine.Object.Instantiate(resultMesh);
            sidecarMesh.name = resultMesh.name;
            try
            {
                if (entry.fbxMesh != null)
                    FbxExport.PreserveUvChannels(sidecarMesh, entry.fbxMesh);
                if (entry.originalMesh != null && entry.originalMesh != entry.fbxMesh)
                {
                    FbxExport.PreserveUvChannels(sidecarMesh, entry.originalMesh);
                    FbxExport.OverwriteUvChannel(sidecarMesh, entry.originalMesh, 1);
                }

                // TBN: keep tangent presence in sync with the source FBX. If the FBX
                // import did not produce tangents, do not let derived/welded meshes
                // smuggle a synthesized tangent stream into the sidecar payload.
                TangentValidator.EnforceTangentsMatchOriginal(sidecarMesh, entry.fbxMesh, "Sidecar");

                Vector2[] auxiliaryUv = null;
                int auxiliaryTargetUvChannel = -1;
                if (ao.IsSet)
                {
                    var uvDonor = FbxExport.SelectUvDonor(entry, resultMesh, ao.channel);
                    if (uvDonor != null)
                    {
                        FbxExport.MergeUvComponentFromDonor(sidecarMesh, uvDonor, ao.channel, ao.component);
                        var auxiliaryUvList = new List<Vector2>();
                        sidecarMesh.GetUVs(ao.channel, auxiliaryUvList);
                        if (auxiliaryUvList.Count == sidecarMesh.vertexCount)
                        {
                            auxiliaryUv = auxiliaryUvList.ToArray();
                            auxiliaryTargetUvChannel = ao.channel;
                        }
                    }
                }

                var primaryUvList = new List<Vector2>();
                sidecarMesh.GetUVs(1, primaryUvList);
                Vector2[] primaryUv = primaryUvList.Count == sidecarMesh.vertexCount ? primaryUvList.ToArray() : null;
                int primaryTargetUvChannel = 1;
                if (primaryUv == null && auxiliaryUv != null)
                {
                    primaryUv = auxiliaryUv;
                    primaryTargetUvChannel = auxiliaryTargetUvChannel;
                    auxiliaryUv = null;
                    auxiliaryTargetUvChannel = -1;
                }
                if (primaryUv == null) return false;

                var positions = sidecarMesh.vertices;
                var colors = sidecarMesh.colors32;
                var uv0List = new List<Vector2>();
                (entry.originalMesh ?? resultMesh).GetUVs(0, uv0List);

                string meshName = entry.fbxMesh != null
                    ? entry.fbxMesh.name
                    : (entry.originalMesh != null ? entry.originalMesh.name : resultMesh.name);
                MeshFingerprint fp = entry.fbxMesh != null ? MeshFingerprint.Compute(entry.fbxMesh) : null;

                sidecarEntry = new MeshUv2Entry
                {
                    meshName = meshName,
                    uv2 = primaryUv,
                    welded = entry.wasWelded,
                    edgeWelded = entry.wasEdgeWelded,
                    vertPositions = positions,
                    vertUv0 = uv0List.ToArray(),
                    optimizedColors = colors.Length == sidecarMesh.vertexCount ? colors : null,
                    schemaVersion = Uv2DataAsset.CurrentSchemaVersion,
                    toolVersion = Uv2DataAsset.ToolVersionStr,
                    sourceFingerprint = fp,
                    targetUvChannel = primaryTargetUvChannel,
                    auxiliaryUv = auxiliaryUv,
                    auxiliaryTargetUvChannel = auxiliaryTargetUvChannel,
                    stepMeshopt = entry.wasWelded,
                    stepEdgeWeld = entry.wasEdgeWelded,
                    stepSymmetrySplit = entry.wasSymmetrySplit,
                    stepRepack = isSourceLod,
                    stepTransfer = !isSourceLod,
                };
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sidecarMesh);
            }
        }

        // ── Collision entries ──

        // Bounds keep malformed project-controlled sidecars from causing large
        // secondary allocations during export. They are deliberately far above
        // the expected size of collision geometry.
        const int MaxHullCount = 1024;
        const int MaxVertexCount = 1_000_000;
        const int MaxIndexCount = 3_000_000;

        /// <summary>
        /// The collision meshes stored next to <paramref name="fbxPath"/>, one list per
        /// entry: a single mesh for a simplified collider, one per hull for a convex
        /// decomposition. The meshes are new objects the caller owns and destroys.
        /// Invalid entries are logged and skipped.
        /// </summary>
        internal static List<(string meshName, List<Mesh> meshes, bool isConvex)> CollisionMeshes(string fbxPath)
        {
            var result = new List<(string, List<Mesh>, bool)>();
            var data = Load(fbxPath);
            if (data == null || data.collisionEntries == null) return result;

            foreach (var entry in data.collisionEntries)
            {
                if (!TryValidateCollisionEntry(entry, out var globalTriangleIndices, out string validationError))
                {
                    UvtLog.Warn($"[Collision] Ignoring invalid sidecar entry: {validationError}");
                    continue;
                }

                bool isConvex = entry.mode == 1;
                var meshes = new List<Mesh>();
                int hullCount = entry.positionOffsets.Length;
                for (int h = 0; h < hullCount; h++)
                {
                    int posStart = entry.positionOffsets[h];
                    int posEnd   = (h + 1 < hullCount) ? entry.positionOffsets[h + 1] : entry.allPositions.Length;
                    int triStart = entry.triangleOffsets[h];
                    int triEnd   = (h + 1 < hullCount) ? entry.triangleOffsets[h + 1] : entry.allTriangles.Length;

                    int vertCount = posEnd - posStart;
                    var verts = new Vector3[vertCount];
                    Array.Copy(entry.allPositions, posStart, verts, 0, vertCount);

                    int idxCount = triEnd - triStart;
                    var tris = new int[idxCount];
                    Array.Copy(entry.allTriangles, triStart, tris, 0, idxCount);
                    if (globalTriangleIndices[h])
                    {
                        // Current sidecars store indices in the flattened vertex array.
                        for (int i = 0; i < tris.Length; i++) tris[i] -= posStart;
                    }

                    var mesh = new Mesh();
                    mesh.name = isConvex ? $"{entry.meshGroupKey}_COL_Hull{h}" : $"{entry.meshGroupKey}_COL";
                    mesh.SetVertices(verts);
                    mesh.SetTriangles(tris, 0);
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    meshes.Add(mesh);
                }
                result.Add((entry.meshGroupKey, meshes, isConvex));
            }
            return result;
        }

        /// <summary>
        /// Checks a collision entry's arrays and offsets, and tells for each hull whether
        /// its triangle indices address the flattened array (current) or the hull's own
        /// vertices (legacy); both encodings are accepted.
        /// </summary>
        internal static bool TryValidateCollisionEntry(CollisionMeshEntry entry, out bool[] globalTriangleIndices, out string error)
        {
            globalTriangleIndices = null;
            error = null;

            if (entry == null)
                return Invalid("entry is null", out error);
            if (entry.mode != 0 && entry.mode != 1)
                return Invalid($"'{entry.meshGroupKey}' has unknown mode {entry.mode}", out error);
            if (entry.allPositions == null || entry.positionOffsets == null ||
                entry.allTriangles == null || entry.triangleOffsets == null)
                return Invalid($"'{entry.meshGroupKey}' has missing mesh arrays", out error);

            int hullCount = entry.positionOffsets.Length;
            if (hullCount == 0 || hullCount > MaxHullCount)
                return Invalid($"'{entry.meshGroupKey}' has invalid hull count {hullCount}", out error);
            if (entry.triangleOffsets.Length != hullCount)
                return Invalid($"'{entry.meshGroupKey}' has mismatched offset arrays", out error);
            if (entry.positionOffsets[0] != 0 || entry.triangleOffsets[0] != 0)
                return Invalid($"'{entry.meshGroupKey}' has non-zero initial offsets", out error);
            if (entry.allPositions.Length > MaxVertexCount || entry.allTriangles.Length > MaxIndexCount)
                return Invalid($"'{entry.meshGroupKey}' exceeds collision mesh size limits", out error);

            globalTriangleIndices = new bool[hullCount];
            for (int h = 0; h < hullCount; h++)
            {
                int posStart = entry.positionOffsets[h];
                int posEnd = h + 1 < hullCount ? entry.positionOffsets[h + 1] : entry.allPositions.Length;
                int triStart = entry.triangleOffsets[h];
                int triEnd = h + 1 < hullCount ? entry.triangleOffsets[h + 1] : entry.allTriangles.Length;

                if (posStart < 0 || posEnd <= posStart || posEnd > entry.allPositions.Length)
                    return Invalid($"'{entry.meshGroupKey}' has invalid vertex range for hull {h}", out error);
                if (triStart < 0 || triEnd <= triStart || triEnd > entry.allTriangles.Length || (triEnd - triStart) % 3 != 0)
                    return Invalid($"'{entry.meshGroupKey}' has invalid triangle range for hull {h}", out error);

                int vertexCount = posEnd - posStart;
                bool canBeLocal = true;
                bool canBeGlobal = true;
                for (int i = triStart; i < triEnd; i++)
                {
                    int index = entry.allTriangles[i];
                    canBeLocal &= index >= 0 && index < vertexCount;
                    canBeGlobal &= index >= posStart && index < posEnd;
                }
                if (!canBeLocal && !canBeGlobal)
                    return Invalid($"'{entry.meshGroupKey}' has out-of-range indices for hull {h}", out error);

                // Before global rebasing was added, multi-hull sidecars stored per-hull
                // local indices. Prefer the current global encoding when a range is
                // valid as both; legacy local data normally contains index zero.
                globalTriangleIndices[h] = canBeGlobal;
            }
            return true;
        }

        static bool Invalid(string message, out string error)
        {
            error = message;
            return false;
        }

        // ── Tool settings ──

        /// <summary>The transfer tool settings stored in the sidecar at <paramref name="sidecarPath"/>, or null.</summary>
        internal static ToolSettings LoadSettings(string sidecarPath)
        {
            if (string.IsNullOrEmpty(sidecarPath)) return null;
            return AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath)?.toolSettings;
        }

        /// <summary>
        /// Lets <paramref name="edit"/> fill the settings block of the sidecar at
        /// <paramref name="sidecarPath"/> (created when missing) and saves it. A no-op
        /// when there is no sidecar.
        /// </summary>
        internal static void SaveSettings(string sidecarPath, Action<ToolSettings> edit)
        {
            if (string.IsNullOrEmpty(sidecarPath) || edit == null) return;
            var data = AssetDatabase.LoadAssetAtPath<Uv2DataAsset>(sidecarPath);
            if (data == null) return;
            if (data.toolSettings == null) data.toolSettings = new ToolSettings();
            edit(data.toolSettings);
            Save(data);
        }
    }
}
