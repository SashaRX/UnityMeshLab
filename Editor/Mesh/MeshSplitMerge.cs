using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Split-by-material and merge-same-material over a LODGroup's renderers: the one
    /// implementation behind the Cleanup and Prefab Builder tabs (each used to carry its
    /// own, and they had drifted — one dropped tangents, colours and UV2 on merge, the
    /// other lost static flags on split). Scan finds the candidates; the two operations
    /// take the candidates the user kept, do the geometry with every vertex attribute
    /// (positions, normals, tangents, colours, bone weights, all eight UV channels at
    /// their stored width), keep the renderer settings and static flags, record Undo for
    /// everything they touch, rewrite the LODGroup in place and record prefab-instance
    /// modifications. Names follow <see cref="MeshNaming"/>.
    /// </summary>
    internal static class MeshSplitMerge
    {
        /// <summary>A renderer whose mesh has several submeshes: one child per material after the split.</summary>
        public sealed class SplitCandidate
        {
            public MeshEntry entry;
            public bool include = true;
        }

        /// <summary>Single-material renderers of one LOD level sharing a material: one mesh after the merge.</summary>
        public sealed class MergeGroup
        {
            public int lodIndex;
            public Material material;
            public List<MeshEntry> entries = new List<MeshEntry>();
            public bool include = true;
        }

        public sealed class Report
        {
            public List<SplitCandidate> split = new List<SplitCandidate>();
            public List<MergeGroup> merge = new List<MergeGroup>();
        }

        /// <summary>Split candidates (multi-submesh meshes) and merge groups (single-material meshes per LOD and material) of the context.</summary>
        public static Report Scan(UvToolContext ctx)
        {
            var report = new Report();
            var mergeMap = new Dictionary<(int lod, int material), MergeGroup>();
            for (int li = 0; li < ctx.LodCount; li++)
                foreach (var e in ctx.ForLod(li))
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;
                    if (mesh.subMeshCount > 1) report.split.Add(new SplitCandidate { entry = e });
                    var mats = e.renderer.sharedMaterials;
                    if (mesh.subMeshCount == 1 && mats.Length == 1 && mats[0] != null)
                    {
                        var key = (li, mats[0].GetInstanceID());
                        if (!mergeMap.TryGetValue(key, out var group))
                            mergeMap[key] = group = new MergeGroup { lodIndex = li, material = mats[0] };
                        group.entries.Add(e);
                    }
                }
            foreach (var group in mergeMap.Values)
                if (group.entries.Count > 1) report.merge.Add(group);
            UvtLog.Info($"Split/Merge scan: {report.split.Count} split candidate(s), {report.merge.Count} merge group(s).");
            return report;
        }

        /// <summary>
        /// Replaces every included candidate by one child per submesh, named
        /// <c>{base}_{material}{lodSuffix}</c> (the LOD suffix stays last so the group
        /// key still pairs them), with the source renderer's settings and static flags,
        /// slotted into the LODGroup where the source was. Returns how many were split.
        /// </summary>
        public static int SplitByMaterial(UvToolContext ctx, IEnumerable<SplitCandidate> candidates, string undoLabel)
        {
            RestorePreviews();
            using var _undo = MeshHygieneUtility.BeginUndoGroup(undoLabel);
            int split = 0;
            foreach (var sc in candidates)
            {
                if (sc == null || !sc.include) continue;
                var e = sc.entry;
                var srcMesh = e?.originalMesh ?? e?.fbxMesh;
                if (srcMesh == null || e.renderer == null || e.meshFilter == null) continue;
                if (!srcMesh.isReadable) { UvtLog.Warn($"Split '{e.renderer.name}': mesh is not readable; enable Read/Write on its importer."); continue; }
                int subCount = srcMesh.subMeshCount;
                if (subCount <= 1) continue;

                var mats = e.renderer.sharedMaterials;
                if (mats.Length != subCount)
                    UvtLog.Warn($"Split '{e.renderer.name}': material count ({mats.Length}) != submesh count ({subCount}) — " +
                                (mats.Length < subCount ? "some submeshes will have no material." : "extra material slots will be dropped."));
                string baseName = MeshNaming.SplitLodSuffix(e.renderer.name, out string lodSuffix);
                var source = e.renderer.transform;
                var staticFlags = GameObjectUtility.GetStaticEditorFlags(e.renderer.gameObject);
                var created = new List<Renderer>();
                for (int s = 0; s < subCount; s++)
                {
                    var tris = srcMesh.GetTriangles(s);
                    if (tris.Length == 0) continue;
                    string matName = s < mats.Length && mats[s] != null ? mats[s].name : $"mat{s}";
                    if (s < mats.Length && mats[s] == null)
                        UvtLog.Warn($"Split '{e.renderer.name}': material slot [{s}] is null — child '{baseName}_mat{s}{lodSuffix}' will have no material.");
                    string childName = $"{baseName}_{matName}{lodSuffix}";
                    var mesh = ExtractSubmesh(srcMesh, tris);
                    mesh.name = childName;

                    var go = new GameObject(childName);
                    Undo.RegisterCreatedObjectUndo(go, undoLabel);
                    go.transform.SetParent(source.parent, false);
                    go.transform.localPosition = source.localPosition;
                    go.transform.localRotation = source.localRotation;
                    go.transform.localScale = source.localScale;
                    GameObjectUtility.SetStaticEditorFlags(go, staticFlags);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = s < mats.Length ? mats[s] : null;
                    RendererSettings.Copy(e.renderer, mr, includeMaterials: false);
                    created.Add(mr);
                }
                if (created.Count == 0)
                {
                    UvtLog.Warn($"Split '{e.renderer.name}': every submesh is empty; nothing to split, the object stays.");
                    continue;
                }
                ReplaceInLodGroup(ctx.LodGroup, e.renderer, created, undoLabel);
                UvtLog.Info($"Split {e.renderer.name}: {subCount} submeshes → {created.Count} objects");
                RemoveSource(e.renderer.gameObject);
                split++;
            }
            UvtLog.Info($"Split {split} multi-material mesh(es).");
            if (split > 0 && ctx.LodGroup != null) ctx.Refresh(ctx.LodGroup);
            return split;
        }

        /// <summary>
        /// Merges every included group into its first renderer: the other renderers'
        /// geometry is brought into the first one's local space with every attribute,
        /// the first object keeps its components, references and static flags and is
        /// renamed <c>{base}_LOD{n}</c>, the others leave the LODGroup and are destroyed.
        /// Returns how many groups were merged.
        /// </summary>
        public static int MergeSameMaterial(UvToolContext ctx, IEnumerable<MergeGroup> groups, string undoLabel)
        {
            RestorePreviews();
            using var _undo = MeshHygieneUtility.BeginUndoGroup(undoLabel);
            int merged = 0;
            foreach (var group in groups)
            {
                if (group == null || !group.include || group.entries.Count < 2) continue;
                var first = group.entries[0];
                if (first.renderer == null || first.meshFilter == null) continue;
                var firstMesh = first.originalMesh ?? first.fbxMesh;
                if (firstMesh == null || !firstMesh.isReadable)
                {
                    // The merge lands in the first renderer; if its own geometry cannot be
                    // read it would be replaced by the others' alone and lost.
                    UvtLog.Warn($"Merge: '{first.renderer.name}' is not readable; enable Read/Write on its importer. Group skipped.");
                    continue;
                }
                var parts = new List<(Mesh mesh, Matrix4x4 toFirst)>();
                var destroy = new List<GameObject>();
                Matrix4x4 worldToFirst = first.renderer.transform.worldToLocalMatrix;
                foreach (var e in group.entries)
                {
                    var mesh = e.originalMesh ?? e.fbxMesh;
                    if (mesh == null || e.renderer == null) continue;
                    if (!mesh.isReadable) { UvtLog.Warn($"Merge: '{e.renderer.name}' is not readable and is skipped."); continue; }
                    parts.Add((mesh, worldToFirst * e.renderer.transform.localToWorldMatrix));
                    if (e != first) destroy.Add(e.renderer.gameObject);
                }
                if (parts.Count < 2) continue;

                string name = MeshNaming.LodName(MeshNaming.StripLod(first.renderer.name), group.lodIndex);
                var mergedMesh = Combine(parts, name);
                Undo.RegisterCreatedObjectUndo(mergedMesh, undoLabel);
                Undo.RecordObject(first.meshFilter, undoLabel);
                Undo.RecordObject(first.renderer, undoLabel);
                Undo.RecordObject(first.renderer.gameObject, undoLabel);
                first.meshFilter.sharedMesh = mergedMesh;
                first.renderer.sharedMaterials = new[] { group.material };
                first.renderer.gameObject.name = name;
                // Refresh restores the context before rebuilding entries. The merge is
                // the new scene baseline, so restoration must keep it alive.
                first.fbxMesh = first.originalMesh = mergedMesh;
                RemoveFromLodGroup(ctx.LodGroup, destroy, undoLabel);
                foreach (var go in destroy)
                {
                    if (go == null) continue;
                    UvtLog.Info($"Merged: {go.name}");
                    Undo.DestroyObjectImmediate(go);
                }
                if (PrefabUtility.IsPartOfPrefabInstance(first.renderer))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(first.renderer);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(first.meshFilter);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(first.renderer.gameObject);
                }
                merged++;
                UvtLog.Info($"Created merged object: {name} ({mergedMesh.vertexCount} verts)");
            }
            UvtLog.Info($"Merged {merged} group(s).");
            if (merged > 0 && ctx.LodGroup != null)
            {
                ctx.Refresh(ctx.LodGroup);
                ctx.LodGroup.RecalculateBounds();
                var lods = ctx.LodGroup.GetLODs();
                for (int li = 0; li < lods.Length; li++)
                    if (lods[li].renderers == null || lods[li].renderers.Length == 0)
                        UvtLog.Warn($"[Merge] LOD{li} has no renderers after merge.");
            }
            return merged;
        }

        /// <summary>
        /// The triangles <paramref name="tris"/> of <paramref name="source"/> as a mesh
        /// of their own: vertices compacted, every attribute the source has copied
        /// (normals, tangents, colours, bone weights, all eight UV channels at their
        /// stored width), one submesh. Readable source required.
        /// </summary>
        public static Mesh ExtractSubmesh(Mesh source, int[] tris)
        {
            if (source == null || tris == null || tris.Length == 0) return null;
            var remap = new Dictionary<int, int>();
            var used = new List<int>();
            foreach (int v in tris) if (!remap.ContainsKey(v)) { remap[v] = used.Count; used.Add(v); }
            int n = used.Count;
            var mesh = new Mesh { name = source.name, indexFormat = n > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            var positions = source.vertices;
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = positions[used[i]];
            mesh.SetVertices(pos);
            var normals = source.normals;
            if (normals != null && normals.Length == positions.Length) { var a = new Vector3[n]; for (int i = 0; i < n; i++) { a[i] = normals[used[i]]; } mesh.normals = a; }
            var tangents = source.tangents;
            if (tangents != null && tangents.Length == positions.Length) { var a = new Vector4[n]; for (int i = 0; i < n; i++) { a[i] = tangents[used[i]]; } mesh.tangents = a; }
            var colors = source.colors;
            if (colors != null && colors.Length == positions.Length) { var a = new Color[n]; for (int i = 0; i < n; i++) { a[i] = colors[used[i]]; } mesh.colors = a; }
            var weights = source.boneWeights;
            if (weights != null && weights.Length == positions.Length) { var a = new BoneWeight[n]; for (int i = 0; i < n; i++) { a[i] = weights[used[i]]; } mesh.boneWeights = a; }
            var uv = new List<Vector4>();
            for (int ch = 0; ch < 8; ch++)
            {
                uv.Clear(); source.GetUVs(ch, uv);
                if (uv.Count != positions.Length) continue;
                int dim = source.HasVertexAttribute(UvAttribute(ch)) ? source.GetVertexAttributeDimension(UvAttribute(ch)) : 2;
                var a = new List<Vector4>(n);
                for (int i = 0; i < n; i++) a.Add(uv[used[i]]);
                if (dim <= 2) mesh.SetUVs(ch, a.Select(v => (Vector2)v).ToList());
                else if (dim == 3) mesh.SetUVs(ch, a.Select(v => (Vector3)v).ToList());
                else mesh.SetUVs(ch, a);
            }
            var newTris = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++) newTris[i] = remap[tris[i]];
            mesh.SetTriangles(newTris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Every part's vertices and directions carried into the first part's space
        // (normals by the inverse transpose; a mirroring matrix flips the part's winding
        // and tangent handedness); attributes a part lacks are filled with neutral values
        // so the channel survives.
        internal static Mesh Combine(List<(Mesh mesh, Matrix4x4 toFirst)> parts, string name)
        {
            bool hasNormals = false, hasTangents = false, hasColors = false;
            var hasUv = new bool[8]; var uvDim = new int[8];
            int totalVerts = 0;
            var probe = new List<Vector4>();
            foreach (var (mesh, _) in parts)
            {
                totalVerts += mesh.vertexCount;
                hasNormals |= mesh.normals != null && mesh.normals.Length == mesh.vertexCount;
                hasTangents |= mesh.tangents != null && mesh.tangents.Length == mesh.vertexCount;
                hasColors |= mesh.colors != null && mesh.colors.Length == mesh.vertexCount;
                for (int ch = 0; ch < 8; ch++)
                {
                    probe.Clear(); mesh.GetUVs(ch, probe);
                    if (probe.Count != mesh.vertexCount) continue;
                    hasUv[ch] = true;
                    int dim = mesh.HasVertexAttribute(UvAttribute(ch)) ? mesh.GetVertexAttributeDimension(UvAttribute(ch)) : 2;
                    uvDim[ch] = Mathf.Max(uvDim[ch], dim);
                }
            }
            var pos = new List<Vector3>(totalVerts);
            var normals = hasNormals ? new List<Vector3>(totalVerts) : null;
            var tangents = hasTangents ? new List<Vector4>(totalVerts) : null;
            var colors = hasColors ? new List<Color>(totalVerts) : null;
            var uvs = new List<Vector4>[8];
            for (int ch = 0; ch < 8; ch++) uvs[ch] = hasUv[ch] ? new List<Vector4>(totalVerts) : null;
            var tris = new List<int>();
            foreach (var (mesh, toFirst) in parts)
            {
                int offset = pos.Count, count = mesh.vertexCount;
                bool mirrored = toFirst.determinant < 0f;
                var normalMatrix = toFirst.inverse.transpose;
                var p = mesh.vertices;
                for (int i = 0; i < count; i++) pos.Add(toFirst.MultiplyPoint3x4(p[i]));
                if (normals != null)
                {
                    var n = mesh.normals;
                    bool own = n != null && n.Length == count;
                    for (int i = 0; i < count; i++) normals.Add(own ? normalMatrix.MultiplyVector(n[i]).normalized : Vector3.up);
                }
                if (tangents != null)
                {
                    var t = mesh.tangents;
                    bool own = t != null && t.Length == count;
                    for (int i = 0; i < count; i++)
                    {
                        if (!own) { tangents.Add(new Vector4(1, 0, 0, 1)); continue; }
                        var d = toFirst.MultiplyVector(new Vector3(t[i].x, t[i].y, t[i].z)).normalized;
                        tangents.Add(new Vector4(d.x, d.y, d.z, mirrored ? -t[i].w : t[i].w));
                    }
                }
                if (colors != null)
                {
                    var c = mesh.colors;
                    bool own = c != null && c.Length == count;
                    for (int i = 0; i < count; i++) colors.Add(own ? c[i] : Color.white);
                }
                for (int ch = 0; ch < 8; ch++)
                {
                    if (uvs[ch] == null) continue;
                    probe.Clear(); mesh.GetUVs(ch, probe);
                    bool own = probe.Count == count;
                    for (int i = 0; i < count; i++) uvs[ch].Add(own ? probe[i] : Vector4.zero);
                }
                var t3 = mesh.triangles;
                if (mirrored)
                    for (int i = 0; i + 2 < t3.Length; i += 3) { tris.Add(t3[i] + offset); tris.Add(t3[i + 2] + offset); tris.Add(t3[i + 1] + offset); }
                else
                    for (int i = 0; i < t3.Length; i++) tris.Add(t3[i] + offset);
            }
            var merged = new Mesh { name = name, indexFormat = pos.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            merged.SetVertices(pos);
            if (normals != null) merged.SetNormals(normals);
            if (tangents != null) merged.SetTangents(tangents);
            if (colors != null) merged.SetColors(colors);
            for (int ch = 0; ch < 8; ch++)
            {
                if (uvs[ch] == null) continue;
                if (uvDim[ch] <= 2) merged.SetUVs(ch, uvs[ch].Select(v => (Vector2)v).ToList());
                else if (uvDim[ch] == 3) merged.SetUVs(ch, uvs[ch].Select(v => (Vector3)v).ToList());
                else merged.SetUVs(ch, uvs[ch]);
            }
            merged.SetTriangles(tris, 0);
            merged.RecalculateBounds();
            return merged;
        }

        // The split source goes away with its mesh — unless the node carries children or
        // components beyond its mesh pair (a collider, a script), in which case the node
        // stays as their container and only its MeshFilter and MeshRenderer are removed.
        static void RemoveSource(GameObject source)
        {
            bool keepNode = source.transform.childCount > 0 || source.GetComponents<Component>().Length > 3;
            if (!keepNode) { Undo.DestroyObjectImmediate(source); return; }
            var mr = source.GetComponent<MeshRenderer>();
            var mf = source.GetComponent<MeshFilter>();
            if (mr != null) Undo.DestroyObjectImmediate(mr);
            if (mf != null) Undo.DestroyObjectImmediate(mf);
            UvtLog.Info($"Split '{source.name}': the node stays as a container for its children / other components.");
        }

        static UnityEngine.Rendering.VertexAttribute UvAttribute(int channel) => UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel;

        // The source renderer's slot in every LOD level takes the created renderers.
        static void ReplaceInLodGroup(LODGroup lodGroup, Renderer source, List<Renderer> created, string undoLabel)
        {
            if (lodGroup == null) return;
            Undo.RecordObject(lodGroup, undoLabel);
            var lods = lodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                var renderers = new List<Renderer>(lods[li].renderers);
                int at = renderers.IndexOf(source);
                if (at < 0) continue;
                renderers.RemoveAt(at);
                renderers.InsertRange(at, created);
                lods[li].renderers = renderers.ToArray();
            }
            lodGroup.SetLODs(lods);
            if (PrefabUtility.IsPartOfPrefabInstance(lodGroup)) PrefabUtility.RecordPrefabInstancePropertyModifications(lodGroup);
        }

        static void RemoveFromLodGroup(LODGroup lodGroup, List<GameObject> gone, string undoLabel)
        {
            if (lodGroup == null) return;
            Undo.RecordObject(lodGroup, undoLabel);
            var lods = lodGroup.GetLODs();
            for (int li = 0; li < lods.Length; li++)
            {
                if (lods[li].renderers == null) continue;
                lods[li].renderers = lods[li].renderers.Where(r => r != null && !gone.Contains(r.gameObject)).ToArray();
            }
            lodGroup.SetLODs(lods);
            if (PrefabUtility.IsPartOfPrefabInstance(lodGroup)) PrefabUtility.RecordPrefabInstancePropertyModifications(lodGroup);
        }

        // A checker or shell-colour preview swaps the renderers' materials; read the
        // real ones, and never let a preview material survive on a new child.
        static void RestorePreviews()
        {
            if (CheckerTexturePreview.IsActive) CheckerTexturePreview.Restore();
            if (ShellColorModelPreview.IsActive) ShellColorModelPreview.Restore();
        }
    }
}
