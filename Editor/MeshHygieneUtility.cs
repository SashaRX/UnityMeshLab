// MeshHygieneUtility.cs — Shared helpers for mesh, name, and collision hygiene.
// Extracted from CleanupTool and LightmapTransferTool so cross-tool logic lives in
// one place and is consistent across tools.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LightmapUvTool
{
    /// <summary>
    /// RAII-style Undo group scope. Opens an undo group on construction, collapses
    /// on dispose. Usage:
    /// <code>using var _scope = MeshHygieneUtility.BeginUndoGroup("Cleanup: Weld");</code>
    /// </summary>
    internal struct UndoGroupScope : IDisposable
    {
        readonly int group;

        public UndoGroupScope(string label)
        {
            group = Undo.GetCurrentGroup();
            if (!string.IsNullOrEmpty(label))
                Undo.SetCurrentGroupName(label);
        }

        public void Dispose()
        {
            Undo.CollapseUndoOperations(group);
        }
    }

    internal static class MeshHygieneUtility
    {
        // ── Compiled regexes (shared, thread-safe) ──

        static readonly System.Text.RegularExpressions.Regex collisionSuffixRegex =
            new System.Text.RegularExpressions.Regex(
                @"_COL(?:_Hull\d+)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static readonly System.Text.RegularExpressions.Regex lodOrColSuffixRegex =
            new System.Text.RegularExpressions.Regex(
                @"[_\-\s]+(LOD\d+|COL\w*|Collider|Collision)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static readonly System.Text.RegularExpressions.Regex invalidCharsRegex =
            new System.Text.RegularExpressions.Regex(
                @"[^A-Za-z0-9_]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static readonly System.Text.RegularExpressions.Regex collapseUnderscoreRegex =
            new System.Text.RegularExpressions.Regex(
                @"_+",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // ── Name helpers ──

        /// <summary>
        /// True for strict collision node naming: ends with <c>_COL</c>, <c>_COL_Hull{N}</c>,
        /// or <c>_Collider</c> (case-insensitive). Used for pipeline hierarchy normalization.
        /// </summary>
        public static bool IsCollisionNodeName(string nodeName)
        {
            if (string.IsNullOrEmpty(nodeName)) return false;
            return collisionSuffixRegex.IsMatch(nodeName) ||
                   nodeName.EndsWith("_Col", StringComparison.OrdinalIgnoreCase) ||
                   nodeName.EndsWith("_Collider", StringComparison.OrdinalIgnoreCase) ||
                   nodeName.EndsWith("_Collision", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True if the name has a trailing LOD/COL/Collider/Collision suffix that a
        /// clean base name should not contain.
        /// </summary>
        public static bool HasLodOrColSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return lodOrColSuffixRegex.IsMatch(name);
        }

        /// <summary>
        /// True if the name contains any characters outside the FBX-safe ASCII
        /// alphanumeric + underscore set.
        /// </summary>
        public static bool HasInvalidChars(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return invalidCharsRegex.IsMatch(name);
        }

        /// <summary>
        /// Replaces non-ASCII-alnum-underscore characters with underscore, collapses
        /// consecutive underscores, trims edges. Returns "Unnamed" for empty results.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            string clean = invalidCharsRegex.Replace(name, "_");
            clean = collapseUnderscoreRegex.Replace(clean, "_");
            clean = clean.Trim('_');
            return string.IsNullOrEmpty(clean) ? "Unnamed" : clean;
        }

        // ── Collision enumeration ──

        /// <summary>
        /// Finds direct children (and their direct children) of <paramref name="root"/>
        /// whose name contains <c>_COL</c> or ends with <c>_Collider</c>. Uses the
        /// historical loose substring match for backwards compatibility with the
        /// original CleanupTool implementation.
        /// </summary>
        public static List<GameObject> FindCollisionObjects(Transform root)
        {
            var result = new List<GameObject>();
            if (root == null) return result;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (IsLooseCollisionName(child.name))
                {
                    result.Add(child.gameObject);
                    for (int j = 0; j < child.childCount; j++)
                    {
                        var gc = child.GetChild(j);
                        if (IsLooseCollisionName(gc.name))
                            result.Add(gc.gameObject);
                    }
                }
            }
            return result;
        }

        static bool IsLooseCollisionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("_COL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.EndsWith("_Col", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_Collider", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_Collision", StringComparison.OrdinalIgnoreCase);
        }

        // ── Mesh stats ──

        /// <summary>
        /// Returns the total triangle count across all submeshes. Safe to call on
        /// non-readable meshes.
        /// </summary>
        public static int GetTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long count = 0;
            int subCount = mesh.subMeshCount;
            for (int i = 0; i < subCount; i++)
                count += mesh.GetIndexCount(i);
            return (int)(count / 3L);
        }

        /// <summary>
        /// Best-effort equality check using vertex/submesh counts, bounds, and (when
        /// both are readable) endpoint vertices. Used to detect duplicate collider
        /// meshes under the same LODGroup.
        /// </summary>
        public static bool AreMeshesDuplicate(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.vertexCount != b.vertexCount) return false;
            if (a.subMeshCount != b.subMeshCount) return false;
            for (int s = 0; s < a.subMeshCount; s++)
            {
                if (a.GetIndexCount(s) != b.GetIndexCount(s)) return false;
            }

            float eps = 0.01f;
            if ((a.bounds.center - b.bounds.center).sqrMagnitude > eps) return false;
            if ((a.bounds.size - b.bounds.size).sqrMagnitude > eps) return false;

            if (a.isReadable && b.isReadable)
            {
                var va = a.vertices;
                var vb = b.vertices;
                if (va.Length == 0) return true;
                float vEps = 1e-5f;
                if ((va[0] - vb[0]).sqrMagnitude > vEps) return false;
                if ((va[va.Length - 1] - vb[vb.Length - 1]).sqrMagnitude > vEps) return false;
            }

            return true;
        }

        // ── Undo group scope factory ──

        public static UndoGroupScope BeginUndoGroup(string label) => new UndoGroupScope(label);

        // ── Mesh mutation helpers ──

        /// <summary>
        /// Prepares a MeshEntry for in-place mutation:
        /// <list type="bullet">
        /// <item>Resolves writable mesh (<c>originalMesh ?? fbxMesh</c>).</item>
        /// <item>Bails out with a warning if missing or non-readable.</item>
        /// <item>Clones asset-backed meshes and re-wires <c>MeshFilter</c> + <c>entry.originalMesh</c>.</item>
        /// <item>Records the resulting mesh and its filter for Undo under <paramref name="undoLabel"/>.</item>
        /// </list>
        /// Returns <c>false</c> when the entry cannot be safely mutated.
        /// </summary>
        public static bool PrepareWritable(MeshEntry entry, string undoLabel, out Mesh mesh)
        {
            mesh = null;
            if (entry == null) return false;
            var source = entry.originalMesh ?? entry.fbxMesh;
            if (source == null) return false;
            if (!source.isReadable)
            {
                UvtLog.Warn($"[Cleanup] Skipped '{source.name}': mesh not readable (enable Read/Write in import settings).");
                return false;
            }

            if (AssetDatabase.Contains(source))
            {
                var clone = UnityEngine.Object.Instantiate(source);
                clone.name = source.name;
                if (entry.meshFilter != null)
                {
                    Undo.RecordObject(entry.meshFilter, undoLabel);
                    entry.meshFilter.sharedMesh = clone;
                }
                entry.originalMesh = clone;
                mesh = clone;
            }
            else
            {
                mesh = source;
            }

            Undo.RecordObject(mesh, undoLabel);
            return true;
        }

        // ── Degenerate triangle detection / removal ──

        /// <summary>
        /// Counts triangles that collapse to a point or line (zero-area faces).
        /// Requires a readable mesh; returns 0 when unreadable.
        /// </summary>
        public static int CountDegenerateTriangles(Mesh mesh, float epsilon = 1e-12f)
        {
            if (mesh == null || !mesh.isReadable) return 0;
            var verts = mesh.vertices;
            int count = 0;
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 == i1 || i0 == i2 || i1 == i2) { count++; continue; }
                    if (i0 < 0 || i0 >= verts.Length ||
                        i1 < 0 || i1 >= verts.Length ||
                        i2 < 0 || i2 >= verts.Length) { count++; continue; }
                    var a = verts[i0];
                    var b = verts[i1];
                    var c = verts[i2];
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < epsilon) count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Rebuilds each submesh's triangle list, skipping degenerate faces. Returns
        /// the number of triangles removed. Caller is responsible for cloning
        /// asset-backed meshes and wrapping in an Undo group.
        /// </summary>
        public static int RemoveDegenerateTriangles(Mesh mesh, float epsilon = 1e-12f)
        {
            if (mesh == null || !mesh.isReadable) return 0;
            var verts = mesh.vertices;
            int removed = 0;
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                var kept = new List<int>(tris.Length);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 == i1 || i0 == i2 || i1 == i2) { removed++; continue; }
                    if (i0 < 0 || i0 >= verts.Length ||
                        i1 < 0 || i1 >= verts.Length ||
                        i2 < 0 || i2 >= verts.Length) { removed++; continue; }
                    var a = verts[i0];
                    var b = verts[i1];
                    var c = verts[i2];
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < epsilon) { removed++; continue; }
                    kept.Add(i0); kept.Add(i1); kept.Add(i2);
                }
                if (kept.Count != tris.Length)
                    mesh.SetTriangles(kept, s);
            }
            if (removed > 0)
                mesh.RecalculateBounds();
            return removed;
        }

        /// <summary>
        /// Returns a per-face boolean mask where true = degenerate triangle.
        /// Face index is global across all submeshes (submesh 0 faces first, then submesh 1, etc.).
        /// Returns null for unreadable meshes.
        /// </summary>
        internal static bool[] GetDegenerateTriangleMask(Mesh mesh, float epsilon = 1e-12f)
        {
            if (mesh == null || !mesh.isReadable) return null;
            var verts = mesh.vertices;
            int totalFaces = 0;
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
                totalFaces += mesh.GetTriangles(s).Length / 3;

            var mask = new bool[totalFaces];
            int fi = 0;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3, fi++)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 == i1 || i0 == i2 || i1 == i2) { mask[fi] = true; continue; }
                    if (i0 < 0 || i0 >= verts.Length ||
                        i1 < 0 || i1 >= verts.Length ||
                        i2 < 0 || i2 >= verts.Length) { mask[fi] = true; continue; }
                    var a = verts[i0];
                    var b = verts[i1];
                    var c = verts[i2];
                    if (Vector3.Cross(b - a, c - a).sqrMagnitude < epsilon) mask[fi] = true;
                }
            }
            return mask;
        }

        // ── Unused vertex detection / compaction ──

        /// <summary>
        /// Returns a per-vertex boolean mask where true = vertex not referenced by any triangle.
        /// Returns null for unreadable meshes.
        /// </summary>
        internal static bool[] GetUnusedVertexMask(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return null;
            var used = new bool[mesh.vertexCount];
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t < tris.Length; t++)
                    if (tris[t] >= 0 && tris[t] < used.Length) used[tris[t]] = true;
            }
            // Invert: true = unused
            var mask = new bool[mesh.vertexCount];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = !used[i];
            return mask;
        }

        /// <summary>
        /// Returns the number of vertices not referenced by any submesh triangle.
        /// Requires a readable mesh.
        /// </summary>
        public static int CountUnusedVertices(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return 0;
            var used = new HashSet<int>();
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t < tris.Length; t++) used.Add(tris[t]);
            }
            return Mathf.Max(0, mesh.vertexCount - used.Count);
        }

        /// <summary>
        /// Removes unreferenced vertices and remaps submesh indices. Skips meshes
        /// with blend shapes (remap can corrupt deltas) and returns 0. Caller owns
        /// cloning and Undo. Returns the number of vertices removed.
        /// </summary>
        public static int CompactVertices(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable) return 0;
            if (mesh.blendShapeCount > 0)
            {
                UvtLog.Warn($"[Cleanup] Skipped CompactVertices on '{mesh.name}' — blend shapes present.");
                return 0;
            }

            int subCount = mesh.subMeshCount;
            var subTris = new int[subCount][];
            for (int s = 0; s < subCount; s++)
                subTris[s] = mesh.GetTriangles(s);

            var oldToNew = new Dictionary<int, int>();
            var order = new List<int>(mesh.vertexCount);
            for (int s = 0; s < subCount; s++)
            {
                var tris = subTris[s];
                for (int t = 0; t < tris.Length; t++)
                {
                    int oi = tris[t];
                    if (!oldToNew.ContainsKey(oi))
                    {
                        oldToNew[oi] = order.Count;
                        order.Add(oi);
                    }
                }
            }

            int srcVertCount = mesh.vertexCount;
            int newCount = order.Count;
            int removed = srcVertCount - newCount;
            if (removed <= 0) return 0;

            var srcPos = mesh.vertices;
            var srcNorm = mesh.normals;
            var srcTan = mesh.tangents;
            var srcColors = mesh.colors;
            var srcBw = mesh.boneWeights;

            bool hasNorm = srcNorm != null && srcNorm.Length == srcVertCount;
            bool hasTan = srcTan != null && srcTan.Length == srcVertCount;
            bool hasCol = srcColors != null && srcColors.Length == srcVertCount;
            bool hasBw = srcBw != null && srcBw.Length == srcVertCount;

            var newPos = new Vector3[newCount];
            var newNorm = hasNorm ? new Vector3[newCount] : null;
            var newTan = hasTan ? new Vector4[newCount] : null;
            var newColors = hasCol ? new Color[newCount] : null;
            var newBw = hasBw ? new BoneWeight[newCount] : null;

            var srcUvs = new List<Vector4>[8];
            var newUvs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var tmp = new List<Vector4>();
                mesh.GetUVs(ch, tmp);
                if (tmp.Count == srcVertCount)
                {
                    srcUvs[ch] = tmp;
                    var dst = new List<Vector4>(newCount);
                    for (int i = 0; i < newCount; i++) dst.Add(default);
                    newUvs[ch] = dst;
                }
            }

            for (int ni = 0; ni < newCount; ni++)
            {
                int oi = order[ni];
                newPos[ni] = srcPos[oi];
                if (newNorm != null) newNorm[ni] = srcNorm[oi];
                if (newTan != null) newTan[ni] = srcTan[oi];
                if (newColors != null) newColors[ni] = srcColors[oi];
                if (newBw != null) newBw[ni] = srcBw[oi];
                for (int ch = 0; ch < 8; ch++)
                    if (newUvs[ch] != null)
                        newUvs[ch][ni] = srcUvs[ch][oi];
            }

            mesh.Clear();
            if (newCount > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(newPos);
            if (newNorm != null) mesh.SetNormals(newNorm);
            if (newTan != null) mesh.SetTangents(newTan);
            if (newColors != null) mesh.SetColors(newColors);
            if (newBw != null) mesh.boneWeights = newBw;
            for (int ch = 0; ch < 8; ch++)
                if (newUvs[ch] != null)
                    mesh.SetUVs(ch, newUvs[ch]);

            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
            {
                var old = subTris[s];
                var remapped = new int[old.Length];
                for (int t = 0; t < old.Length; t++)
                    remapped[t] = oldToNew[old[t]];
                mesh.SetTriangles(remapped, s);
            }
            mesh.RecalculateBounds();
            return removed;
        }

        // ── Import Settings scan ──

        /// <summary>
        /// Describes a destructive Unity import flag on a model asset that conflicts
        /// with the tool's mesh/UV2 workflow.
        /// </summary>
        public struct ImportSettingsIssue
        {
            public enum Kind
            {
                GenerateSecondaryUV,
                WeldVertices,
                MeshCompression,
                MeshOptimization,
                NotReadable,
            }
            public string assetPath;
            public Kind kind;
            public string description;
            public bool isHardIssue; // false = informational
        }

        /// <summary>
        /// Scans a ModelImporter for flags that conflict with the tool's UV2/mesh
        /// workflow. See <see cref="Uv2AssetPostprocessor.PrepareImportSettings"/>
        /// for the canonical enforcement logic — this scan only reports state.
        /// </summary>
        public static void ScanImportSettings(string assetPath, List<ImportSettingsIssue> sink)
        {
            if (string.IsNullOrEmpty(assetPath) || sink == null) return;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return;

            string fbxName = System.IO.Path.GetFileName(assetPath);

            if (importer.generateSecondaryUV)
            {
                sink.Add(new ImportSettingsIssue
                {
                    assetPath = assetPath,
                    kind = ImportSettingsIssue.Kind.GenerateSecondaryUV,
                    isHardIssue = true,
                    description = $"{fbxName}: generateSecondaryUV is ON — Unity will overwrite authored UV2 on reimport.",
                });
            }
            if (importer.weldVertices)
            {
                sink.Add(new ImportSettingsIssue
                {
                    assetPath = assetPath,
                    kind = ImportSettingsIssue.Kind.WeldVertices,
                    isHardIssue = true,
                    description = $"{fbxName}: weldVertices is ON — Unity will merge vertices by position and break UV shells.",
                });
            }
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                sink.Add(new ImportSettingsIssue
                {
                    assetPath = assetPath,
                    kind = ImportSettingsIssue.Kind.MeshCompression,
                    isHardIssue = true,
                    description = $"{fbxName}: meshCompression is {importer.meshCompression} — quantization breaks UV2 precision.",
                });
            }
            // Unity 2022.1+ unified the two booleans into this bitmask.
            var optFlags = importer.meshOptimizationFlags;
            if (optFlags != 0)
            {
                sink.Add(new ImportSettingsIssue
                {
                    assetPath = assetPath,
                    kind = ImportSettingsIssue.Kind.MeshOptimization,
                    isHardIssue = true,
                    description = $"{fbxName}: meshOptimizationFlags is {optFlags} — Unity reorders mesh data and invalidates sidecar fingerprints.",
                });
            }
            if (!importer.isReadable)
            {
                sink.Add(new ImportSettingsIssue
                {
                    assetPath = assetPath,
                    kind = ImportSettingsIssue.Kind.NotReadable,
                    isHardIssue = false,
                    description = $"{fbxName}: Read/Write is OFF — mesh fixers must clone before mutating.",
                });
            }
        }
    }
}
