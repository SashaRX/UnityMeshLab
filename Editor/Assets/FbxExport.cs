// FbxExport.cs — the mechanics of re-saving an FBX from Mesh Lab: the export mesh for
// an entry (result geometry, the source's UV channels, the AO component, matched
// tangents), the isolated channel re-save (snapshot the intended channels, clone the
// FBX prefab, write only those, atomic file replace), the LOD-rebuild hierarchy pieces
// (mesh replacement by name, new LOD children, stale-child pruning, hierarchy
// normalisation, collision injection and stripping, material trims), the write itself
// and the post-reimport scene relink. Tools keep the policy — dialogs, backups,
// importer locks, which entries, what to refresh — and call these.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab
{
    internal static class FbxExport
    {
        const string MetaExtension = ".meta";
        const string RelinkUndoLabel = "Relink Mesh";

        // ─────────────────────────────────────────────────────────────────
        // Names
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The FBX node name an entry exports under: the FBX sub-asset's name, else the
        /// working mesh's, else the result's, else the renderer's; transient preview names
        /// ("Hidden/…") fall through to the renderer. "Mesh" when nothing is named.
        /// </summary>
        internal static string ResolveExportMeshName(MeshEntry entry, Mesh resultMesh)
        {
            if (entry?.fbxMesh != null && !string.IsNullOrEmpty(entry.fbxMesh.name))
                return entry.fbxMesh.name;

            string fallback = entry?.originalMesh != null ? entry.originalMesh.name : null;
            if (string.IsNullOrEmpty(fallback) && resultMesh != null)
                fallback = resultMesh.name;

            if (!string.IsNullOrEmpty(fallback) &&
                (fallback.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase) ||
                 fallback.StartsWith("Hidden_", StringComparison.OrdinalIgnoreCase)) &&
                entry?.renderer != null && !string.IsNullOrEmpty(entry.renderer.name))
            {
                return entry.renderer.name;
            }

            if (!string.IsNullOrEmpty(fallback)) return fallback;
            if (entry?.renderer != null && !string.IsNullOrEmpty(entry.renderer.name)) return entry.renderer.name;
            return "Mesh";
        }

        /// <summary>
        /// A mesh name a DCC round-trip left behind (`Scene`, `Geometry`, `Default`, `Mesh`,
        /// `Combined Mesh…`) or no name at all.
        /// </summary>
        internal static bool IsGenericMeshName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name)
            {
                case "Scene":
                case "Geometry":
                case "Default":
                case "Mesh":
                case "Combined Mesh":
                    return true;
                default:
                    return name.StartsWith("Combined Mesh", StringComparison.Ordinal);
            }
        }

        /// <summary>A material name an importer invents (`Lit`, `Default`, `No Name`…) or no name at all.</summary>
        internal static bool IsPlaceholderMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            switch (name)
            {
                case "Lit":
                case "Default":
                case "Material":
                case "DefaultMaterial":
                case "Default-Material":
                case "No Name":
                    return true;
                default:
                    return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // UV channels
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Copies the UV channels <paramref name="exportMesh"/> lacks from
        /// <paramref name="sourceMesh"/> (same vertex count required). A 2D channel that is
        /// all zeros is not worth carrying; existing channels are never overwritten.
        /// </summary>
        internal static void PreserveUvChannels(Mesh exportMesh, Mesh sourceMesh)
        {
            if (exportMesh == null || sourceMesh == null) return;
            if (sourceMesh.vertexCount != exportMesh.vertexCount) return;
            for (int ch = 0; ch < 8; ch++)
            {
                var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (exportMesh.HasVertexAttribute(attr)) continue;
                if (!sourceMesh.HasVertexAttribute(attr)) continue;

                int dim = sourceMesh.GetVertexAttributeDimension(attr);
                if (dim <= 2)
                {
                    var uv = new List<Vector2>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count == 0) continue;
                    bool allZero = true;
                    for (int i = 0; i < uv.Count; i++)
                        if (uv[i].x != 0f || uv[i].y != 0f) { allZero = false; break; }
                    if (allZero) continue;
                    exportMesh.SetUVs(ch, uv);
                }
                else if (dim == 3)
                {
                    var uv = new List<Vector3>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count > 0) exportMesh.SetUVs(ch, uv);
                }
                else
                {
                    var uv = new List<Vector4>();
                    sourceMesh.GetUVs(ch, uv);
                    if (uv.Count > 0) exportMesh.SetUVs(ch, uv);
                }
            }
        }

        /// <summary>Replaces UV channel <paramref name="channel"/> of <paramref name="exportMesh"/> with the source's, at the source's width.</summary>
        internal static void OverwriteUvChannel(Mesh exportMesh, Mesh sourceMesh, int channel)
        {
            if (exportMesh == null || sourceMesh == null) return;
            if (channel < 0 || channel > 7) return;
            if (sourceMesh.vertexCount != exportMesh.vertexCount) return;
            var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!sourceMesh.HasVertexAttribute(attr)) return;

            int dim = sourceMesh.GetVertexAttributeDimension(attr);
            if (dim <= 2)
            {
                var uv = new List<Vector2>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount) exportMesh.SetUVs(channel, uv);
            }
            else if (dim == 3)
            {
                var uv = new List<Vector3>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount) exportMesh.SetUVs(channel, uv);
            }
            else
            {
                var uv = new List<Vector4>();
                sourceMesh.GetUVs(channel, uv);
                if (uv.Count == exportMesh.vertexCount) exportMesh.SetUVs(channel, uv);
            }
        }

        /// <summary>
        /// Writes one component (0 = X, 1 = Y) of the donor's UV channel into the export
        /// mesh's, keeping the other component. A missing export channel starts as the donor's.
        /// </summary>
        internal static void MergeUvComponentFromDonor(Mesh exportMesh, Mesh donorMesh, int uvChannel, int uvComponent)
        {
            if (exportMesh == null || donorMesh == null) return;
            if (exportMesh.vertexCount != donorMesh.vertexCount) return;
            if (uvChannel < 0 || uvChannel > 7) return;
            if (uvComponent < 0 || uvComponent > 1) return;

            var donorUv = new List<Vector2>();
            donorMesh.GetUVs(uvChannel, donorUv);
            if (donorUv.Count != exportMesh.vertexCount) return;

            var exportUv = new List<Vector2>();
            exportMesh.GetUVs(uvChannel, exportUv);
            if (exportUv.Count != exportMesh.vertexCount)
                exportUv = new List<Vector2>(donorUv);

            for (int i = 0; i < exportUv.Count; i++)
            {
                var src = donorUv[i];
                var dst = exportUv[i];
                exportUv[i] = uvComponent == 0 ? new Vector2(src.x, dst.y) : new Vector2(dst.x, src.y);
            }
            exportMesh.SetUVs(uvChannel, exportUv);
        }

        /// <summary>True when the mesh carries UV channel <paramref name="channel"/> for every vertex.</summary>
        internal static bool HasUvChannelData(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel > 7) return false;
            var attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!mesh.HasVertexAttribute(attr)) return false;

            int dim = mesh.GetVertexAttributeDimension(attr);
            int vCount = mesh.vertexCount;
            if (dim <= 2)
            {
                var uv = new List<Vector2>();
                mesh.GetUVs(channel, uv);
                return uv.Count == vCount;
            }
            if (dim == 3)
            {
                var uv = new List<Vector3>();
                mesh.GetUVs(channel, uv);
                return uv.Count == vCount;
            }
            var uv4 = new List<Vector4>();
            mesh.GetUVs(channel, uv4);
            return uv4.Count == vCount;
        }

        /// <summary>
        /// The mesh of an entry that carries UV channel <paramref name="uvChannel"/>: AO
        /// is written on the working or FBX mesh, so those come first and the transfer
        /// result last (its UV1 stays authoritative while AO comes from the donor).
        /// </summary>
        internal static Mesh SelectUvDonor(MeshEntry entry, Mesh resultMesh, int uvChannel)
        {
            var candidates = new[] { entry?.originalMesh, entry?.fbxMesh, entry?.repackedMesh, entry?.transferredMesh, resultMesh };
            for (int i = 0; i < candidates.Length; i++)
                if (HasUvChannelData(candidates[i], uvChannel)) return candidates[i];
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // The export mesh of one entry
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The temporary mesh written for an entry: a copy of <paramref name="resultMesh"/>
        /// named <see cref="ResolveExportMeshName"/>, with the UV channels of the FBX mesh
        /// and then the working mesh carried over, UV1 taken from the working mesh when the
        /// entry has no repack or transfer result, the AO component merged from the best
        /// donor, and tangents matched to the FBX import. Added to <paramref name="tempSink"/>;
        /// the caller destroys it after the write.
        /// </summary>
        internal static Mesh BuildExportMesh(MeshEntry entry, Mesh resultMesh, SidecarStore.AoUvTarget ao, List<Mesh> tempSink)
        {
            if (tempSink == null) throw new ArgumentNullException(nameof(tempSink));
            var exportMesh = Object.Instantiate(resultMesh);
            tempSink.Add(exportMesh);
            // Without an explicit name, Object.Instantiate produces "X(Clone)" and Unity's
            // FBX Exporter falls back to the FBX scene name ("Scene") when writing the
            // FbxMesh node — every reimported mesh ends up named "Scene". Pin the canonical
            // name now so the FBX node and post-reimport mesh asset stay aligned.
            exportMesh.name = ResolveExportMeshName(entry, resultMesh);

            // UV channels from fbxMesh first (base UVs), then from originalMesh (has AO
            // and other tool modifications).
            if (entry.fbxMesh != null)
                PreserveUvChannels(exportMesh, entry.fbxMesh);
            if (entry.originalMesh != null && entry.originalMesh != entry.fbxMesh)
            {
                PreserveUvChannels(exportMesh, entry.originalMesh);
                // Only overwrite UV1 from originalMesh when there is no repack/transfer
                // result — otherwise the repacked lightmap UV in channel 1 takes priority.
                if (entry.repackedMesh == null && entry.transferredMesh == null)
                    OverwriteUvChannel(exportMesh, entry.originalMesh, 1);
            }
            // AO often lives in a UV component. Source meshes may not have that channel
            // at all, so pick the best available donor.
            if (ao.IsSet)
            {
                var donor = SelectUvDonor(entry, resultMesh, ao.channel);
                if (donor != null)
                    MergeUvComponentFromDonor(exportMesh, donor, ao.channel, ao.component);
            }
            TangentValidator.EnforceTangentsMatchOriginal(exportMesh, entry.fbxMesh, "FBX Export");
            return exportMesh;
        }

        // ─────────────────────────────────────────────────────────────────
        // Which FBX
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The FBX the entries came from: the first entry's FBX sub-asset path, else the
        /// FBX behind the LODGroup's prefab source or one of its renderers, else
        /// <paramref name="fallback"/>. Null when nothing resolves.
        /// </summary>
        internal static string ResolveSourceFbxPath(IEnumerable<MeshEntry> entries, LODGroup lodGroup, string fallback)
        {
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    if (e?.fbxMesh == null) continue;
                    string p = AssetDatabase.GetAssetPath(e.fbxMesh);
                    if (SidecarStore.IsFbxPath(p)) return p;
                }
            }
            if (lodGroup != null)
            {
                var prefabSrc = PrefabUtility.GetCorrespondingObjectFromSource(lodGroup.gameObject);
                if (prefabSrc != null)
                {
                    string p = AssetDatabase.GetAssetPath(prefabSrc);
                    if (SidecarStore.IsFbxPath(p)) return p;
                }
                foreach (var r in lodGroup.GetComponentsInChildren<Renderer>(true))
                {
                    var rSrc = PrefabUtility.GetCorrespondingObjectFromSource(r);
                    if (rSrc == null) continue;
                    string p = AssetDatabase.GetAssetPath(rSrc);
                    if (SidecarStore.IsFbxPath(p)) return p;
                }
            }
            return string.IsNullOrEmpty(fallback) ? null : fallback;
        }

        /// <summary>
        /// The included entries with a result, grouped by the FBX they export into: the
        /// entry's own FBX when it has one, else <paramref name="sourceFbxPath"/> (generated
        /// LODs live in `.asset` files). Entries with neither are left out.
        /// </summary>
        internal static Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>> GroupByFbx(
            IEnumerable<MeshEntry> entries, Func<MeshEntry, Mesh> resultMeshOf, string sourceFbxPath)
        {
            var groups = new Dictionary<string, List<(MeshEntry entry, Mesh resultMesh)>>();
            if (entries == null) return groups;
            foreach (var e in entries)
            {
                if (e == null || !e.include) continue;
                Mesh resultMesh = resultMeshOf(e);
                if (resultMesh == null) continue;
                Mesh pathMesh = e.fbxMesh ?? e.originalMesh;
                string fbxPath = pathMesh != null ? AssetDatabase.GetAssetPath(pathMesh) : null;
                if (!SidecarStore.IsFbxPath(fbxPath)) fbxPath = sourceFbxPath;
                if (string.IsNullOrEmpty(fbxPath)) continue;
                if (!groups.TryGetValue(fbxPath, out var list))
                    groups[fbxPath] = list = new List<(MeshEntry, Mesh)>();
                list.Add((e, resultMesh));
            }
            return groups;
        }

        /// <summary>The first entry material that is not a preview shader; the collision nodes' material.</summary>
        internal static Material FirstRealMaterial(IEnumerable<MeshEntry> entries)
        {
            if (entries == null) return null;
            foreach (var e in entries)
            {
                var mat = e?.renderer != null ? e.renderer.sharedMaterial : null;
                if (mat != null && !CheckerTexturePreview.IsPreviewShader(mat.shader.name)) return mat;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        // Isolated-channel re-save (per FbxExportIntent)
        //
        // Re-saves the source FBX overwriting only the per-vertex channels listed in the
        // intent. All other data — node names, hierarchy, transforms, material
        // assignments, untouched UV channels, vertex colors, normals, tangents — is
        // inherited from the source FBX asset on disk via clone-and-snapshot.
        // ─────────────────────────────────────────────────────────────────

        internal static void ReimportWithoutSidecars(string path, Action import)
        {
            bool added = Uv2AssetPostprocessor.bypassPaths.Add(path);
            try { import(); }
            finally { if (added) Uv2AssetPostprocessor.bypassPaths.Remove(path); }
        }

        internal static Dictionary<string, Snapshot> BuildChannelSnapshots(IEnumerable<MeshEntry> entries, FbxExportIntent intent)
        {
            var snapshots = new Dictionary<string, Snapshot>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || !entry.include) continue;
                var source = entry.originalMesh ?? entry.fbxMesh;
                var donor = entry.repackedMesh ?? entry.transferredMesh ?? source;
                var identity = entry.fbxMesh ?? source;
                string name = identity != null ? identity.name : null;
                if (donor != null && !string.IsNullOrEmpty(name)) snapshots[name] = BuildSnapshot(donor, intent);
            }
            return snapshots;
        }

        internal sealed class Snapshot
        {
            public int vertexCount;
            public Color32[] colors32;
            public Color[]   colors;
            public Vector3[] normals;
            public Vector4[] tangents;
            public readonly Vector2[][] uvs = new Vector2[8][];
        }

        /// <summary>The channels of <paramref name="source"/> the intent covers, captured before any reimport.</summary>
        internal static Snapshot BuildSnapshot(Mesh source, FbxExportIntent intent)
        {
            var snap = new Snapshot { vertexCount = source.vertexCount };
            if ((intent & FbxExportIntent.VertexColors) != 0)
            {
                var c32 = source.colors32;
                if (c32 != null && c32.Length == source.vertexCount)
                    snap.colors32 = c32;
                else
                {
                    var c = source.colors;
                    if (c != null && c.Length == source.vertexCount) snap.colors = c;
                }
            }
            if ((intent & FbxExportIntent.Normals) != 0)
            {
                var n = source.normals;
                if (n != null && n.Length == source.vertexCount) snap.normals = n;
            }
            if ((intent & FbxExportIntent.Tangents) != 0)
            {
                var t = source.tangents;
                if (t != null && t.Length == source.vertexCount) snap.tangents = t;
            }
            for (int ch = 0; ch < 8; ch++)
            {
                if (!intent.IncludesUv(ch)) continue;
                var list = new List<Vector2>();
                source.GetUVs(ch, list);
                if (list.Count == source.vertexCount) snap.uvs[ch] = list.ToArray();
            }
            return snap;
        }

        /// <summary>
        /// Writes the snapshots onto fresh copies of the clone's meshes (matched by
        /// sub-asset name; the live FBX sub-assets are never mutated). Every copy goes to
        /// <paramref name="tempSink"/>. Returns the number of channel updates.
        /// </summary>
        internal static int CopySnapshotsToClone(GameObject tempRoot, Dictionary<string, Snapshot> snapshots, List<Mesh> tempSink)
        {
            if (tempSink == null) throw new ArgumentNullException(nameof(tempSink));
            if (snapshots == null) return 0;
            int updated = 0, visited = 0, matched = 0;
            foreach (var cloneMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (cloneMf == null || cloneMf.sharedMesh == null) continue;
                visited++;
                if (!snapshots.TryGetValue(cloneMf.sharedMesh.name, out var snap)) continue;
                matched++;

                if (snap.vertexCount != cloneMf.sharedMesh.vertexCount)
                {
                    UvtLog.Warn($"[FBX Export] Skip '{cloneMf.sharedMesh.name}': vertex-count mismatch " +
                        $"(authored={snap.vertexCount}, FBX clone={cloneMf.sharedMesh.vertexCount}). " +
                        "The source FBX re-imports at a different vertex count than the tool worked on — " +
                        "usually 'Generate Lightmap UVs' splitting vertices. Disable it on the model " +
                        "importer and re-run the tool.");
                    continue;
                }

                var cloneMesh = Object.Instantiate(cloneMf.sharedMesh);
                cloneMesh.name = cloneMf.sharedMesh.name;
                tempSink.Add(cloneMesh);

                if (snap.colors32 != null) { cloneMesh.colors32 = snap.colors32; updated++; }
                else if (snap.colors != null) { cloneMesh.colors = snap.colors; updated++; }
                if (snap.normals != null)  { cloneMesh.normals  = snap.normals;  updated++; }
                if (snap.tangents != null) { cloneMesh.tangents = snap.tangents; updated++; }
                for (int ch = 0; ch < 8; ch++)
                {
                    if (snap.uvs[ch] == null || snap.uvs[ch].Length != cloneMesh.vertexCount) continue;
                    cloneMesh.SetUVs(ch, snap.uvs[ch]);
                    updated++;
                }
                cloneMf.sharedMesh = cloneMesh;
            }
            UvtLog.Verbose($"[FBX Export] CopySnapshotsToClone: visited={visited}, matched={matched}, updates={updated}.");
            return updated;
        }

        /// <summary>
        /// Puts a source ModelImporter back to the state the export found it in; a no-op
        /// when nothing was changed. Created before the importer is touched and told about
        /// each change before the reimport that applies it, so a throwing reimport is
        /// covered too. Disposed at the export's exit whichever way it leaves.
        /// </summary>
        internal sealed class ImporterRestoreScope : IDisposable
        {
            readonly string path;
            ModelImporter importer;
            bool readable, quads;

            public ImporterRestoreScope(string path) { this.path = path; }

            /// <summary>isReadable was flipped on for the export; put it back off.</summary>
            public void RestoreReadable(ModelImporter source) { importer = source; readable = true; }
            /// <summary>keepQuads was toggled on for the export; put it back off.</summary>
            public void RestoreQuads(ModelImporter source) { importer = source; quads = true; }

            public void Dispose()
            {
                if (importer == null || (!readable && !quads)) return;
                if (readable) importer.isReadable = false;
                if (quads) importer.keepQuads = false;
                ReimportWithoutSidecars(path, importer.SaveAndReimport);
            }
        }

        /// <summary>
        /// Soft pre-export checks on the cloned hierarchy against the FBX pipeline
        /// checklist: generic or invalid node and mesh names, placeholder materials, vertex
        /// colours outside [0,1] (when the intent writes them), negative-determinant
        /// scales. Every finding is a warning; none blocks the export.
        /// </summary>
        internal static void RunPreflight(GameObject tempRoot, FbxExportIntent intent, Dictionary<string, Snapshot> snapshots)
        {
            if (tempRoot == null) return;

            int badNodeNames = 0, badMeshNames = 0;
            foreach (var t in tempRoot.GetComponentsInChildren<Transform>(true))
                if (string.IsNullOrEmpty(t.name) || MeshHygieneUtility.HasInvalidChars(t.name)) badNodeNames++;
            foreach (var mf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && IsGenericMeshName(mf.sharedMesh.name)) badMeshNames++;
            foreach (var smr in tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null && IsGenericMeshName(smr.sharedMesh.name)) badMeshNames++;
            if (badNodeNames > 0)
                UvtLog.Warn($"[FBX Preflight] {badNodeNames} node name(s) are empty or contain invalid characters (see §5.5/§8).");
            if (badMeshNames > 0)
                UvtLog.Warn($"[FBX Preflight] {badMeshNames} mesh(es) have a generic name (Scene/Geometry/Default/empty); see §5.5.");

            int placeholderMats = 0;
            foreach (var mr in tempRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = mr.sharedMaterials;
                if (mats == null) continue;
                foreach (var mat in mats)
                    if (mat == null || IsPlaceholderMaterialName(mat.name)) placeholderMats++;
            }
            if (placeholderMats > 0)
                UvtLog.Warn($"[FBX Preflight] {placeholderMats} placeholder material slot(s) (Lit/Default/null); see §1.5.");

            if ((intent & FbxExportIntent.VertexColors) != 0 && snapshots != null)
            {
                int outOfRangeMeshes = 0;
                foreach (var snap in snapshots.Values)
                {
                    var c = snap.colors;
                    if (c == null) continue; // colors32 is byte-clamped by definition
                    for (int i = 0; i < c.Length; i++)
                    {
                        var v = c[i];
                        if (v.r < 0f || v.r > 1f || v.g < 0f || v.g > 1f || v.b < 0f || v.b > 1f || v.a < 0f || v.a > 1f)
                        { outOfRangeMeshes++; break; }
                    }
                }
                if (outOfRangeMeshes > 0)
                    UvtLog.Warn($"[FBX Preflight] {outOfRangeMeshes} mesh(es) have vertex colors outside [0,1]; see §4.2.");
            }

            int negScaleNodes = 0;
            foreach (var t in tempRoot.GetComponentsInChildren<Transform>(true))
            {
                var s = t.lossyScale;
                if (s.x * s.y * s.z < 0f) negScaleNodes++;
            }
            if (negScaleNodes > 0)
                UvtLog.Warn($"[FBX Preflight] {negScaleNodes} node(s) have negative-determinant accumulated scale (mesh will render transparent from front); see §7.8.");
        }

        /// <summary>
        /// Re-saves the FBX at <paramref name="sourceFbxPath"/> (or writes a new file at
        /// <paramref name="outputFbxPath"/>) overwriting only the channels in
        /// <paramref name="intent"/>; everything else comes from the source FBX on disk.
        /// UV and vertex-colour intents edit the FBX document in place
        /// (<see cref="FbxChannelWrite"/>): only the changed corners of the changed channels
        /// are written, and the source importer is left alone except for switching off
        /// 'Generate Lightmap UVs' when UV1 is written. Any other intent takes the clone path:
        /// importer prep (one reimport, scoped to the intent; the source importer
        /// ends as it started except for the deliberate generateSecondaryUV/keepQuads
        /// locks), clone and overwrite, atomic write, reimport and relink of
        /// <paramref name="sceneRoot"/>'s references (source re-save only). The caller
        /// restores its previews before and its working meshes after. Returns true when
        /// the file was written.
        /// </summary>
        /// <param name="collisionMaterial">The material `_COL` renderers get when the intent includes Materials; null destroys their renderers.</param>
        internal static bool WriteChannels(
            string sourceFbxPath,
            IEnumerable<MeshEntry> entries,
            FbxExportIntent intent,
            string outputFbxPath,
            Material collisionMaterial,
            LODGroup sceneRoot)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (string.IsNullOrEmpty(sourceFbxPath) || entries == null) return false;

            // UV sets and vertex colours are written into the FBX document itself; nothing
            // else in the file is rebuilt (see FbxChannelWrite).
            if (FbxChannelWrite.Handles(intent))
            {
                try { return FbxChannelWrite.Write(sourceFbxPath, entries, intent, outputFbxPath, sceneRoot); }
                catch (Exception ex)
                {
                    UvtLog.Error($"[FBX Export] '{sourceFbxPath}' was not written: {ex.Message}");
                    UvtLog.Verbose(ex.ToString());
                    return false;
                }
            }

            // Normals, tangents, hierarchy and material intents still re-export the file
            // through Unity's FBX Exporter (polygons are rebuilt from Unity's triangles).
            string targetFbxPath = string.IsNullOrEmpty(outputFbxPath) ? sourceFbxPath : outputFbxPath;
            bool isVariantExport = !string.IsNullOrEmpty(outputFbxPath)
                && !string.Equals(outputFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);

            // Snapshot pre-export. Phase 1 can trigger a reimport that resets the shared
            // FBX sub-asset buffers in place — keying by sub-asset name lets us find the
            // original data after the reimport.
            var snapshots = BuildChannelSnapshots(entries, intent);
            if (snapshots.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No source meshes had data for intent {intent}.");
                return false;
            }

            // ── Phase 1: importer ──
            using var importerRestore = new ImporterRestoreScope(sourceFbxPath);
            var srcImporter = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            if (!isVariantExport)
            {
                if (srcImporter != null)
                {
                    bool needsReimport = false;
                    // generateSecondaryUV writes Unity UV channel 1. Lock it only when the
                    // intent overwrites that channel, and leave it off: re-enabling it would
                    // regenerate channel 1 on the restore reimport over the authored UV1.
                    if (intent.IncludesUv(1) && srcImporter.generateSecondaryUV)
                        { srcImporter.generateSecondaryUV = false; needsReimport = true; }
                    // weldVertices / meshCompression / meshOptimizationFlags are deliberately
                    // left alone: the snapshot and the clone both come from the current
                    // import and share its vertex layout; a reimport with different settings
                    // would renumber the clone and every mesh would be skipped.
                    if (!srcImporter.isReadable)
                        { srcImporter.isReadable = true; needsReimport = true; importerRestore.RestoreReadable(srcImporter); }
                    // keepQuads changes only the index buffer (quads stay quads through
                    // the exporter); the vertex stream is untouched, so the snapshots still
                    // match. It persists on purpose: restoring it would triangulate the
                    // just-written quad FBX on the next import.
                    if (!srcImporter.keepQuads)
                        { srcImporter.keepQuads = true; needsReimport = true; }
                    if (needsReimport)
                    {
                        ReimportWithoutSidecars(sourceFbxPath, srcImporter.SaveAndReimport);
                    }
                }
            }
            else if (srcImporter != null && !srcImporter.keepQuads && AssetDatabase.LoadMainAssetAtPath(sourceFbxPath) != null)
            {
                // A variant writes a NEW file and must leave the source importer unchanged,
                // but the clone needs the source's quad topology; toggle keepQuads for the
                // clone reimport and put it back at exit.
                srcImporter.keepQuads = true;
                importerRestore.RestoreQuads(srcImporter);
                ReimportWithoutSidecars(sourceFbxPath, srcImporter.SaveAndReimport);
            }

            // ── Phase 2: clone and overwrite ──
            var fbxAsset = AssetDatabase.LoadMainAssetAtPath(sourceFbxPath) as GameObject;
            if (fbxAsset == null)
            {
                UvtLog.Error($"[FBX Export] Cannot load FBX asset at '{sourceFbxPath}'.");
                return false;
            }
            var tempRoot = Object.Instantiate(fbxAsset);
            tempRoot.name = fbxAsset.name;

            bool exported = false;
            Dictionary<string, string> renameMap = null;
            var tempMeshes = new List<Mesh>();
            try
            {
                int updated = CopySnapshotsToClone(tempRoot, snapshots, tempMeshes);
                if (updated == 0 && (intent & (FbxExportIntent.Hierarchy | FbxExportIntent.Materials)) == 0)
                {
                    // Hierarchy / Materials intents restructure the FBX without per-vertex
                    // changes; a per-vertex-only intent with no matching mesh writes nothing.
                    UvtLog.Warn($"[FBX Export] No matching meshes in clone for intent {intent}.");
                    return false;
                }
                if ((intent & FbxExportIntent.Hierarchy) != 0)
                    renameMap = NormalizeExportHierarchy(tempRoot, tempMeshes);
                if ((intent & FbxExportIntent.Materials) != 0)
                {
                    PrepareCollisionMaterials(tempRoot, collisionMaterial);
                    TrimMaterialArrays(tempRoot);
                }
                RunPreflight(tempRoot, intent, snapshots);

                // ── Phase 3: write ──
                // Signal the UV2 postprocessor to skip sidecar injection on the reimport the
                // write triggers — otherwise an isolated UV2 export would be overwritten by
                // stale sidecar data at once.
                Uv2AssetPostprocessor.fbxOverwritePaths.Add(targetFbxPath);
                WriteAtomic(targetFbxPath, tempRoot, (intent & FbxExportIntent.Hierarchy) != 0);
                UvtLog.Info($"[FBX Export] Isolated channels {intent} ({updated} updates) -> {targetFbxPath}");
                exported = true;
            }
            catch (Exception ex)
            {
                Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath);
                UvtLog.Error("[FBX Export] Isolated channel export failed: " + ex);
                return false;
            }
            finally
            {
                Object.DestroyImmediate(tempRoot);
                DestroyTempMeshes(tempMeshes);
            }

            // ── Phase 4: reimport and relink ──
            AssetDatabase.Refresh();
            if ((intent & FbxExportIntent.Hierarchy) != 0) {
                var normalizedImporter = AssetImporter.GetAtPath(targetFbxPath) as ModelImporter;
                if (normalizedImporter != null) {
                    normalizedImporter.globalScale = 1;
                    normalizedImporter.useFileScale = true;
                    normalizedImporter.bakeAxisConversion = true;
                    ReimportWithoutSidecars(targetFbxPath, normalizedImporter.SaveAndReimport);
                }
            }
            if (isVariantExport)
            {
                // The variant was written with the source's quad topology; pin keepQuads
                // on its importer so the project view matches the file.
                var outImporter = AssetImporter.GetAtPath(targetFbxPath) as ModelImporter;
                if (outImporter != null && !outImporter.keepQuads)
                {
                    outImporter.keepQuads = true;
                    outImporter.SaveAndReimport();
                }
            }
            else if (sceneRoot != null)
            {
                // renameMap is non-null only when the intent included Hierarchy; narrow
                // per-vertex intents re-bind purely by sub-asset name.
                RelinkSceneMeshReferences(sourceFbxPath, renameMap != null && renameMap.Count > 0 ? renameMap : null, sceneRoot);
            }
            return exported;
#else
            UvtLog.Error("[FBX Export] FBX Exporter package not installed.");
            return false;
#endif
        }

        // ─────────────────────────────────────────────────────────────────
        // Writing
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes <paramref name="root"/> to <paramref name="fbxPath"/> as a binary FBX and
        /// throws when the exporter produced nothing. For new files; a re-save of an
        /// existing asset goes through <see cref="WriteAtomic"/>.
        /// </summary>
        internal static void Write(string fbxPath, GameObject root, bool embedTextures = false, bool normalizedTransforms = false)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            ExportFile(fbxPath, root, embedTextures, normalizedTransforms);
            if (normalizedTransforms) ConvertNormalizedFile(fbxPath, embedTextures);
            var info = new FileInfo(Path.GetFullPath(fbxPath));
            if (!info.Exists || info.Length == 0)
                throw new IOException($"FBX Exporter produced an empty/missing file at '{fbxPath}'.");
#else
            throw new InvalidOperationException("FBX Exporter package not installed.");
#endif
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        static void ExportFile(string path, GameObject root, bool embedTextures, bool normalizedTransforms)
        {
            GameObject exportRoot = root;
            var copies = new List<Mesh>();
            try {
                if (normalizedTransforms) {
                    exportRoot = Object.Instantiate(root);
                    exportRoot.name = root.name;
                    // Unity's exporter changes handedness by reflecting X; Unity's
                    // importer reflects Z. Rotate geometry (including its tangent frame)
                    // so this does not introduce a 180-degree turn on reimport. Keep
                    // node transforms at identity for the DCC file.
                    var orientation = Matrix4x4.Scale(new Vector3(-1, 1, -1));
                    foreach (var filter in exportRoot.GetComponentsInChildren<MeshFilter>(true)) {
                        if (filter.sharedMesh == null) continue;
                        var mesh = Object.Instantiate(filter.sharedMesh);
                        mesh.name = filter.sharedMesh.name;
                        copies.Add(mesh); filter.sharedMesh = mesh;
                        MeshTransform.BakeMatrix(mesh, orientation);
                    }
                }
                UnityEditor.Formats.Fbx.Exporter.ModelExporter.ExportObjects(path, new Object[] { exportRoot },
                    new UnityEditor.Formats.Fbx.Exporter.ExportModelOptions {
                        ExportFormat = UnityEditor.Formats.Fbx.Exporter.ExportFormat.Binary,
                        EmbedTextures = embedTextures });
            }
            finally {
                if (exportRoot != root) Object.DestroyImmediate(exportRoot);
                DestroyTempMeshes(copies);
            }
        }

        // Unity's exporter always writes centimeters/Y-up. A meter-based Max scene
        // imports that with 1% scale. Convert coordinates as well as metadata; setting
        // only UnitScaleFactor would enlarge the geometry 100 times.
        static void ConvertNormalizedFile(string path, bool embedTextures)
        {
            string fullPath = Path.GetFullPath(path);
            string converted = fullPath + ".normalized.tmp";
            string mediaScratch = Path.Combine(Path.GetTempPath(), "meshlab-fbx-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(mediaScratch);
                string input = Path.Combine(mediaScratch, "input.fbx");
                File.Copy(fullPath, input);
                using var manager = FbxManager.Create();
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, embedTextures);
                var scene = FbxScene.Create(manager, "NormalizedMeshLabExport");
                using (var importer = FbxImporter.Create(manager, "MeshLabImport")) {
                    if (!importer.Initialize(input, -1, io) || !importer.Import(scene))
                        throw new IOException("Cannot read FBX for transform normalization.");
                }
                FbxAxisSystem.Max.DeepConvertScene(scene);
                double factor = scene.GetGlobalSettings().GetSystemUnit().GetScaleFactor() / FbxSystemUnit.m.GetScaleFactor();
                var meshes = new HashSet<FbxMesh>();
                var nodes = new Stack<FbxNode>();
                nodes.Push(scene.GetRootNode());
                while (nodes.Count > 0) {
                    var node = nodes.Pop();
                    var p = node.LclTranslation.Get(); var r = node.LclRotation.Get(); var s = node.LclScaling.Get();
                    for (int axis = 0; axis < 3; ++axis)
                        if (Math.Abs(p[axis]) > 1e-6 || Math.Abs(r[axis]) > 1e-6 || Math.Abs(s[axis] - 1) > 1e-6)
                            throw new IOException("Normalized FBX contains an unbaked node transform.");
                    var mesh = node.GetMesh();
                    if (mesh != null && meshes.Add(mesh))
                        for (int i = 0; i < mesh.GetControlPointsCount(); ++i) {
                            var v = mesh.GetControlPointAt(i);
                            for (int axis = 0; axis < 3; ++axis) v[axis] *= factor;
                            mesh.SetControlPointAt(v, i);
                        }
                    for (int i = 0; i < node.GetChildCount(); ++i) nodes.Push(node.GetChild(i));
                }
                scene.GetGlobalSettings().SetSystemUnit(FbxSystemUnit.m);
                using (var exporter = FbxExporter.Create(manager, "MeshLabExport")) {
                    if (!exporter.Initialize(converted, -1, io) || !exporter.Export(scene))
                        throw new IOException("Cannot write normalized FBX.");
                }
                File.Copy(converted, fullPath, true);
            }
            finally {
                if (File.Exists(converted)) File.Delete(converted);
                if (Directory.Exists(mediaScratch)) Directory.Delete(mediaScratch, true);
            }
        }
#endif

        /// <summary>
        /// Writes <paramref name="root"/> to `<target>.tmp`, verifies it, and replaces the
        /// target in one rename (a plain move for a new path), keeping the target's `.meta`
        /// as it was. If the exporter throws or writes an empty file the target on disk is
        /// untouched. Throws on failure after removing the temp file.
        /// </summary>
        internal static void WriteAtomic(string targetFbxPath, GameObject root, bool normalizedTransforms = false)
        {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            ReplaceAtomically(targetFbxPath, tmpRelPath =>
            {
                ExportFile(tmpRelPath, root, false, normalizedTransforms);
                if (normalizedTransforms) ConvertNormalizedFile(Path.GetFullPath(tmpRelPath), false);
            });
#else
            throw new InvalidOperationException("FBX Exporter package not installed.");
#endif
        }

        /// <summary>
        /// Lets <paramref name="write"/> produce the file at `<target>.tmp`, verifies it, and
        /// replaces the target in one rename (a plain move for a new path), keeping the
        /// target's `.meta` as it was. If the writer throws or writes an empty file the
        /// target on disk is untouched. Throws on failure after removing the temp file.
        /// </summary>
        internal static void ReplaceAtomically(string targetFbxPath, Action<string> write)
        {
            string fullPath = Path.GetFullPath(targetFbxPath);
            // Hash the full path so two FBX files with the same filename get distinct
            // backup names. unchecked cast rather than Math.Abs — Math.Abs(int.MinValue) throws.
            string pathHash = unchecked((uint)fullPath.GetHashCode()).ToString("X8");
            string metaBak = Path.Combine(Path.GetTempPath(), Path.GetFileName(fullPath) + "." + pathHash + ".meta.bak");
            bool metaBackedUp = File.Exists(fullPath + MetaExtension);
            if (metaBackedUp) File.Copy(fullPath + MetaExtension, metaBak, true);

            string tmpRelPath = targetFbxPath + ".tmp";
            string tmpAbsPath = Path.GetFullPath(tmpRelPath);
            if (File.Exists(tmpAbsPath)) File.Delete(tmpAbsPath); // leftover from a crashed run
            try
            {
                write(tmpRelPath);
                var tmpInfo = new FileInfo(tmpAbsPath);
                if (!tmpInfo.Exists || tmpInfo.Length == 0)
                    throw new IOException($"FBX writer produced an empty/missing file at '{tmpRelPath}'.");

                // File.Replace needs an existing target (overwrite + backup); a fresh path
                // is a move.
                if (File.Exists(fullPath))
                {
                    string fbxBak = Path.Combine(Path.GetTempPath(), Path.GetFileName(fullPath) + "." + pathHash + ".fbx.bak");
                    File.Replace(tmpAbsPath, fullPath, fbxBak);
                    if (File.Exists(fbxBak)) File.Delete(fbxBak);
                }
                else
                {
                    File.Move(tmpAbsPath, fullPath);
                }

                // The exporter may have generated a .meta for the .tmp — strip it so the
                // AssetDatabase does not pick up a ghost asset on the next refresh.
                string tmpMetaPath = tmpAbsPath + MetaExtension;
                if (File.Exists(tmpMetaPath)) File.Delete(tmpMetaPath);

                if (metaBackedUp && File.Exists(metaBak))
                {
                    File.Copy(metaBak, fullPath + MetaExtension, true);
                    File.Delete(metaBak);
                }
            }
            catch
            {
                // Best-effort: drop a leftover .tmp so a retry is not blocked. The original
                // error matters, so this never throws.
                try { if (File.Exists(tmpAbsPath)) File.Delete(tmpAbsPath); }
                catch { /* the leftover .tmp is cosmetic; the exception below is the real error */ }
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // LOD-rebuild hierarchy
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// If the cloned FBX carries its visible mesh on the root object, moves it to a new
        /// child named after the mesh so the rest of the pipeline treats it as a LOD entry
        /// (<see cref="NormalizeExportHierarchy"/> then renames it to `baseName_LOD0`).
        /// Skipped when a direct child already holds that mesh.
        /// </summary>
        internal static void PromoteRootMeshToLod0Child(GameObject tempRoot)
        {
            if (tempRoot == null) return;
            var rootMf = tempRoot.GetComponent<MeshFilter>();
            if (rootMf == null || rootMf.sharedMesh == null) return;
            var rootMr = tempRoot.GetComponent<MeshRenderer>();
            var rootMesh = rootMf.sharedMesh;

            for (int ci = 0; ci < tempRoot.transform.childCount; ci++)
            {
                var existing = tempRoot.transform.GetChild(ci).GetComponent<MeshFilter>();
                if (existing != null && existing.sharedMesh == rootMesh) return;
            }

            string childName = rootMesh.name;
            if (string.IsNullOrEmpty(childName)) childName = tempRoot.name;

            var lod0 = new GameObject(childName);
            lod0.transform.SetParent(tempRoot.transform, false);
            lod0.transform.localPosition = Vector3.zero;
            lod0.transform.localRotation = Quaternion.identity;
            lod0.transform.localScale = Vector3.one;
            lod0.AddComponent<MeshFilter>().sharedMesh = rootMesh;
            if (rootMr != null)
            {
                var newMr = lod0.AddComponent<MeshRenderer>();
                RendererSettings.Copy(rootMr, newMr);
                GameObjectUtility.SetStaticEditorFlags(lod0, GameObjectUtility.GetStaticEditorFlags(tempRoot));
                Object.DestroyImmediate(rootMr);
            }
            Object.DestroyImmediate(rootMf);
        }

        /// <summary>The renderer of the highest LOD among the entries; the template for new LOD children.</summary>
        internal static Renderer FindLastLodRenderer(IEnumerable<(MeshEntry entry, Mesh resultMesh)> entries)
        {
            Renderer best = null;
            int bestLod = int.MinValue;
            foreach (var (entry, _) in entries)
            {
                if (entry?.renderer == null) continue;
                if (entry.lodIndex >= bestLod) { bestLod = entry.lodIndex; best = entry.renderer; }
            }
            return best;
        }

        /// <summary>
        /// Swaps the clone's meshes for the export meshes of the same name (MeshFilters and
        /// SkinnedMeshRenderers) and copies the matching scene renderer's settings onto the
        /// node. Returns the names that were replaced.
        /// </summary>
        internal static HashSet<string> ReplaceMeshes(GameObject tempRoot, Dictionary<string, Mesh> replacements, Dictionary<string, Renderer> rendererTemplates)
        {
            var replaced = new HashSet<string>();
            foreach (var mf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !replacements.TryGetValue(mf.sharedMesh.name, out var replacement)) continue;
                string meshName = mf.sharedMesh.name;
                replaced.Add(meshName);
                mf.sharedMesh = replacement;
                if (rendererTemplates.TryGetValue(meshName, out var srcRenderer))
                {
                    var dstRenderer = mf.GetComponent<MeshRenderer>();
                    if (dstRenderer != null) RendererSettings.Copy(srcRenderer, dstRenderer);
                }
            }
            foreach (var smr in tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null || !replacements.TryGetValue(smr.sharedMesh.name, out var replacement)) continue;
                string meshName = smr.sharedMesh.name;
                replaced.Add(meshName);
                smr.sharedMesh = replacement;
                if (rendererTemplates.TryGetValue(meshName, out var srcRenderer))
                    RendererSettings.Copy(srcRenderer, smr);
            }
            return replaced;
        }

        /// <summary>
        /// Adds a LOD child for a mesh the clone did not have (a generated LOD): a direct
        /// child named <paramref name="meshName"/> at the entry renderer's local transform,
        /// replacing any same-named child from a previous export. Renderer settings and
        /// static flags come from <paramref name="template"/> (the last LOD's renderer) or
        /// the entry's own renderer.
        /// </summary>
        internal static void AddLodChild(GameObject tempRoot, string meshName, Mesh exportMesh, Renderer entryRenderer, Renderer template)
        {
            for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
            {
                var ch = tempRoot.transform.GetChild(ci);
                if (ch.name == meshName) Object.DestroyImmediate(ch.gameObject);
            }
            var child = new GameObject(meshName);
            child.transform.SetParent(tempRoot.transform, false);
            if (entryRenderer != null)
            {
                child.transform.localPosition = entryRenderer.transform.localPosition;
                child.transform.localRotation = entryRenderer.transform.localRotation;
                child.transform.localScale = entryRenderer.transform.localScale;
            }
            child.AddComponent<MeshFilter>().sharedMesh = exportMesh;
            var mr = child.AddComponent<MeshRenderer>();
            var from = template ?? entryRenderer;
            if (from != null)
            {
                RendererSettings.Copy(from, mr);
                GameObjectUtility.SetStaticEditorFlags(child, GameObjectUtility.GetStaticEditorFlags(from.gameObject));
            }
        }

        /// <summary>
        /// Destroys the clone's direct children that render a mesh outside
        /// <paramref name="validMeshNames"/> (by node name or mesh name). Collision nodes,
        /// nodes with a MeshCollider or whose mesh a collider uses, and container nodes
        /// without a mesh are kept. Must run before <see cref="NormalizeExportHierarchy"/>,
        /// which renames LOD0. Returns the number pruned.
        /// </summary>
        internal static int PruneStaleChildren(GameObject tempRoot, HashSet<string> validMeshNames)
        {
            var colliderMeshNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var colliderRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mc in tempRoot.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc == null) continue;
                colliderRootNames.Add(mc.gameObject.name);
                if (mc.sharedMesh != null && !string.IsNullOrEmpty(mc.sharedMesh.name))
                    colliderMeshNames.Add(mc.sharedMesh.name);
            }

            int pruned = 0;
            for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
            {
                var ch = tempRoot.transform.GetChild(ci);
                if (MeshNaming.IsCollision(ch.name)) continue;
                if (colliderRootNames.Contains(ch.name)) continue;
                var chMf = ch.GetComponent<MeshFilter>();
                if (chMf != null && chMf.sharedMesh != null && colliderMeshNames.Contains(chMf.sharedMesh.name)) continue;
                var chSmr = ch.GetComponent<SkinnedMeshRenderer>();
                string childMeshName = chMf != null && chMf.sharedMesh != null ? chMf.sharedMesh.name
                                     : chSmr != null && chSmr.sharedMesh != null ? chSmr.sharedMesh.name : null;
                // Structural/container nodes stay: removing them flattens the hierarchy
                // and can break prefabs.
                if (childMeshName == null) continue;
                // Some imports name the node differently from its mesh (root LOD0
                // especially); either name counts.
                if (validMeshNames.Contains(ch.name) || validMeshNames.Contains(childMeshName)) continue;
                UvtLog.Verbose($"[FBX Export] Pruning stale child '{ch.name}'");
                Object.DestroyImmediate(ch.gameObject);
                pruned++;
            }
            return pruned;
        }

        /// <summary>
        /// Normalizes static export geometry at the root pivot, preserving its world size
        /// and orientation with identity node transforms. The direct
        /// child named like the root renamed `_LOD0`, each LOD chain renumbered to
        /// contiguous `_LOD0.._LODN` per prefix. Accumulated transforms are baked into mesh
        /// copies before ancestors are reset. Returns oldName → newName for the renamed nodes (for the
        /// post-reimport scene relink). Every mesh copy goes to <paramref name="bakedMeshSink"/>;
        /// the caller destroys them after the write.
        /// </summary>
        internal static Dictionary<string, string> NormalizeExportHierarchy(GameObject root, List<Mesh> bakedMeshSink)
        {
            if (bakedMeshSink == null) throw new ArgumentNullException(nameof(bakedMeshSink));

            var renameMap = new Dictionary<string, string>();
            string baseName = root.name;
            string sanitizedBaseName = MeshHygieneUtility.SanitizeName(baseName);
            if (string.IsNullOrEmpty(sanitizedBaseName)) sanitizedBaseName = "Unnamed";

            BakeNormalizedStaticHierarchy(root, bakedMeshSink);

            foreach (Transform child in root.transform)
            {
                if (child.name == baseName || child.name == sanitizedBaseName)
                {
                    string oldName = child.name;
                    child.name = sanitizedBaseName + "_LOD0";
                    if (oldName != child.name) renameMap[oldName] = child.name;
                    break;
                }
            }

            // Contiguous LOD numbering PER GROUP (the child's own prefix before `_LOD<N>`),
            // so several LOD chains under one root keep their prefixes. Prevents importer
            // warnings ("_LOD1 found but no _LOD0") after inconsistent sanitising.
            var groupedLodChildren = new Dictionary<string, List<(Transform transform, int index, int siblingIndex)>>();
            var groupOrder = new List<string>();
            foreach (Transform child in root.transform)
            {
                if (MeshNaming.IsCollision(child.name)) continue;
                var mf = child.GetComponent<MeshFilter>();
                var smr = child.GetComponent<SkinnedMeshRenderer>();
                bool hasMesh = (mf != null && mf.sharedMesh != null) || (smr != null && smr.sharedMesh != null);
                if (!hasMesh) continue;

                // Any LOD index the name carries, even one the LODGroup cannot hold: this
                // pass only groups and orders children.
                string groupPrefix = MeshNaming.SplitLodSuffix(child.name, out string lodSuffix);
                if (lodSuffix.Length == 0 ||
                    !int.TryParse(lodSuffix.Substring(lodSuffix.ToUpperInvariant().LastIndexOf("LOD") + 3), out int parsedIndex))
                    continue;
                if (!groupedLodChildren.TryGetValue(groupPrefix, out var list))
                {
                    groupedLodChildren[groupPrefix] = list = new List<(Transform, int, int)>();
                    groupOrder.Add(groupPrefix);
                }
                list.Add((child, parsedIndex, child.GetSiblingIndex()));
            }
            foreach (var groupPrefix in groupOrder)
            {
                var list = groupedLodChildren[groupPrefix];
                list.Sort((a, b) =>
                {
                    int cmp = a.index.CompareTo(b.index);
                    return cmp != 0 ? cmp : a.siblingIndex.CompareTo(b.siblingIndex);
                });
                for (int i = 0; i < list.Count; i++)
                {
                    string normalizedName = groupPrefix + "_LOD" + i;
                    string oldName = list[i].transform.name;
                    if (oldName == normalizedName) continue;
                    list[i].transform.name = normalizedName;
                    renameMap[oldName] = normalizedName;
                }
            }

            return renameMap;
        }

        internal static void BakeNormalizedStaticHierarchy(GameObject root, List<Mesh> bakedMeshSink)
        {
            // Bone bind poses and animation cannot be preserved by a static vertex bake.
            if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                throw new InvalidOperationException("Transform normalization supports static meshes only; export skinned models without hierarchy normalization.");
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var matrices = new Matrix4x4[filters.Length];
            var recenter = Matrix4x4.Translate(-root.transform.position);
            // Capture every accumulated matrix before changing any ancestor. This also
            // preserves shear from rotated children under non-uniformly scaled parents.
            for (int i = 0; i < filters.Length; ++i) {
                matrices[i] = recenter * filters[i].transform.localToWorldMatrix;
                var mesh = filters[i].sharedMesh;
                if (mesh != null && !mesh.isReadable)
                    throw new InvalidOperationException($"Cannot normalize unreadable mesh '{mesh.name}'. Enable Read/Write before exporting.");
            }
            for (int i = 0; i < filters.Length; ++i) {
                var mesh = filters[i].sharedMesh;
                if (mesh == null || matrices[i] == Matrix4x4.identity) continue;
                var baked = Object.Instantiate(mesh);
                baked.name = mesh.name;
                bakedMeshSink.Add(baked);
                MeshTransform.BakeMatrix(baked, matrices[i]);
                filters[i].sharedMesh = baked;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true)) group.RecalculateBounds();
        }

        /// <summary>
        /// Adds the sidecar's collision meshes as `_COL` children (one node for a
        /// simplified collider, a container with `_COL_Hull{N}` children for a convex
        /// decomposition), removing the clone's existing `_COL` children first when there
        /// is anything to add. The meshes go to <paramref name="tempSink"/>. Returns the
        /// number of collision meshes placed.
        /// </summary>
        internal static int InjectCollisionMeshes(GameObject tempRoot, List<(string meshName, List<Mesh> meshes, bool isConvex)> collisionData, List<Mesh> tempSink)
        {
            if (tempSink == null) throw new ArgumentNullException(nameof(tempSink));
            if (collisionData == null || collisionData.Count == 0) return 0;

            for (int ci = tempRoot.transform.childCount - 1; ci >= 0; ci--)
            {
                var ch = tempRoot.transform.GetChild(ci);
                if (MeshNaming.IsCollision(ch.name)) Object.DestroyImmediate(ch.gameObject);
            }

            int count = 0;
            foreach (var (colMeshName, colMeshes, isConvex) in collisionData)
            {
                tempSink.AddRange(colMeshes);
                if (colMeshes.Count == 1 && !isConvex)
                {
                    // No MeshRenderer: avoids a stale material on the node.
                    var colChild = new GameObject(colMeshName + "_COL");
                    colChild.transform.SetParent(tempRoot.transform, false);
                    colChild.AddComponent<MeshFilter>().sharedMesh = colMeshes[0];
                    count++;
                }
                else
                {
                    var container = new GameObject(colMeshName + "_COL");
                    container.transform.SetParent(tempRoot.transform, false);
                    for (int hi = 0; hi < colMeshes.Count; hi++)
                    {
                        var hullChild = new GameObject($"{colMeshName}_COL_Hull{hi}");
                        hullChild.transform.SetParent(container.transform, false);
                        hullChild.AddComponent<MeshFilter>().sharedMesh = colMeshes[hi];
                        count++;
                    }
                }
            }
            if (count > 0) UvtLog.Verbose($"[FBX Export] Added {count} collision mesh(es) from sidecar");
            return count;
        }

        /// <summary>
        /// Gives every `_COL` node a renderer with <paramref name="fallback"/> (so the
        /// exporter does not write a default "Lit" material), or destroys its renderer
        /// when there is no material to give.
        /// </summary>
        internal static void PrepareCollisionMaterials(GameObject tempRoot, Material fallback)
        {
            foreach (var colMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (colMf == null || colMf.sharedMesh == null) continue;
                if (!MeshNaming.IsCollision(colMf.gameObject.name)) continue;
                var colMr = colMf.GetComponent<MeshRenderer>();
                if (fallback != null)
                {
                    if (colMr == null) colMr = colMf.gameObject.AddComponent<MeshRenderer>();
                    colMr.sharedMaterials = new[] { fallback };
                }
                else if (colMr != null)
                    Object.DestroyImmediate(colMr);
            }
        }

        /// <summary>
        /// Reduces every readable `_COL` mesh to positions, triangles, recalculated normals
        /// and (only when the source had them) synthesized tangents — no UVs or colours —
        /// after <see cref="PrepareCollisionMaterials"/>. The stripped copies go to
        /// <paramref name="tempSink"/>; a Read/Write-disabled collision sub-asset is warned
        /// about and left alone.
        /// </summary>
        internal static void StripCollisionMeshes(GameObject tempRoot, Material fallback, List<Mesh> tempSink)
        {
            if (tempSink == null) throw new ArgumentNullException(nameof(tempSink));
            PrepareCollisionMaterials(tempRoot, fallback);
            foreach (var colMf in tempRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (colMf == null || colMf.sharedMesh == null) continue;
                if (!MeshNaming.IsCollision(colMf.gameObject.name)) continue;
                var srcCol = colMf.sharedMesh;
                if (!srcCol.isReadable)
                {
                    UvtLog.Warn($"[FBX Export] Collision mesh '{srcCol.name}' is not readable — " +
                                "skipping strip. Re-save collision to sidecar to fix.");
                    continue;
                }
                var stripped = new Mesh { name = srcCol.name };
                tempSink.Add(stripped);
                stripped.SetVertices(srcCol.vertices);
                for (int s = 0; s < srcCol.subMeshCount; s++)
                    stripped.SetTriangles(srcCol.GetTriangles(s), s);
                stripped.RecalculateNormals();
                if (TangentValidator.HasTangents(srcCol))
                {
                    var normals = stripped.normals;
                    var tangents = new Vector4[normals.Length];
                    for (int ti = 0; ti < normals.Length; ti++)
                    {
                        Vector3 n = normals[ti];
                        Vector3 t = Vector3.Cross(n, Vector3.up);
                        if (t.sqrMagnitude < 0.001f) t = Vector3.Cross(n, Vector3.right);
                        t.Normalize();
                        tangents[ti] = new Vector4(t.x, t.y, t.z, 1f);
                    }
                    stripped.tangents = tangents;
                    TangentValidator.ValidateTangentsW(tangents, stripped.name, "FBX Export (collision)");
                }
                stripped.RecalculateBounds();
                colMf.sharedMesh = stripped;
            }
        }

        /// <summary>Cuts every renderer's material array down to its mesh's submesh count (no spurious default "Lit" slots).</summary>
        internal static void TrimMaterialArrays(GameObject tempRoot)
        {
            foreach (var mr in tempRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mats = mr.sharedMaterials;
                if (mats.Length <= mf.sharedMesh.subMeshCount) continue;
                UvtLog.Verbose($"[FBX Export] Trimming materials on '{mr.gameObject.name}': {mats.Length} → {mf.sharedMesh.subMeshCount}");
                var trimmed = new Material[mf.sharedMesh.subMeshCount];
                Array.Copy(mats, trimmed, trimmed.Length);
                mr.sharedMaterials = trimmed;
            }
        }

        /// <summary>
        /// Destroys the temporary meshes an export built. DestroyImmediate on the temp root
        /// frees only GameObjects, so the copies have to be released explicitly — and only
        /// after the write, which reads them.
        /// </summary>
        internal static void DestroyTempMeshes(List<Mesh> meshes)
        {
            if (meshes == null) return;
            foreach (var mesh in meshes)
                if (mesh != null) Object.DestroyImmediate(mesh);
            meshes.Clear();
        }

        // ─────────────────────────────────────────────────────────────────
        // After the reimport
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-binds the MeshFilters and MeshColliders under <paramref name="lodGroup"/> to
        /// the reimported FBX's sub-assets (Unity recreates them, so old references go
        /// Missing even when names did not change), by mesh name, node name, the
        /// <paramref name="renameMap"/> (optional) or a fuzzy name match, and renames
        /// scene nodes that the export renamed. Records Undo for every change.
        /// </summary>
        internal static void RelinkSceneMeshReferences(string fbxPath, Dictionary<string, string> renameMap, LODGroup lodGroup)
        {
            if (lodGroup == null) return;

            var meshByName = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                if (asset is Mesh mesh && !meshByName.ContainsKey(mesh.name)) meshByName[mesh.name] = mesh;
            if (meshByName.Count == 0) return;

            int relinked = 0;
            var root = lodGroup.transform;

            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null) continue;
                if (mf.sharedMesh != null)
                {
                    string meshName = mf.sharedMesh.name;
                    if (renameMap != null && renameMap.TryGetValue(meshName, out string newName)) meshName = newName;
                    if (meshByName.TryGetValue(meshName, out var freshMesh) && mf.sharedMesh != freshMesh)
                    {
                        Undo.RecordObject(mf, RelinkUndoLabel);
                        mf.sharedMesh = freshMesh;
                        relinked++;
                    }
                    continue;
                }

                // Missing mesh — by node name, then the renamed name, then fuzzy.
                string goName = mf.gameObject.name;
                if (meshByName.TryGetValue(goName, out var match))
                {
                    Undo.RecordObject(mf, RelinkUndoLabel);
                    mf.sharedMesh = match;
                    relinked++;
                    continue;
                }
                if (renameMap != null)
                {
                    foreach (var kvp in renameMap)
                    {
                        if (goName != kvp.Key || !meshByName.TryGetValue(kvp.Value, out var renamedMesh)) continue;
                        Undo.RecordObject(mf, RelinkUndoLabel);
                        mf.sharedMesh = renamedMesh;
                        relinked++;
                        break;
                    }
                }
                if (mf.sharedMesh == null)
                {
                    foreach (var kvp in meshByName)
                    {
                        if (!goName.Contains(kvp.Key) && !kvp.Key.Contains(goName)) continue;
                        Undo.RecordObject(mf, RelinkUndoLabel);
                        mf.sharedMesh = kvp.Value;
                        relinked++;
                        break;
                    }
                }
            }

            foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc == null) continue;
                if (mc.sharedMesh != null)
                {
                    string meshName = mc.sharedMesh.name;
                    if (renameMap != null && renameMap.TryGetValue(meshName, out string newName)) meshName = newName;
                    if (meshByName.TryGetValue(meshName, out var freshMesh) && mc.sharedMesh != freshMesh)
                    {
                        Undo.RecordObject(mc, "Relink Collider Mesh");
                        mc.sharedMesh = freshMesh;
                        relinked++;
                    }
                }
                else if (meshByName.TryGetValue(mc.gameObject.name, out var match))
                {
                    Undo.RecordObject(mc, "Relink Collider Mesh");
                    mc.sharedMesh = match;
                    relinked++;
                }
            }

            if (renameMap != null)
            {
                foreach (var kvp in renameMap)
                {
                    foreach (Transform child in root)
                    {
                        if (child.name != kvp.Key) continue;
                        Undo.RecordObject(child.gameObject, "Rename to match FBX");
                        child.name = kvp.Value;
                        break;
                    }
                }
            }

            if (relinked > 0)
                UvtLog.Info($"[FBX Export] Relinked {relinked} mesh reference(s) after reimport.");
        }

        /// <summary>
        /// Removes the importer's external material remaps named `Lit` or `No Name` (the
        /// defaults it invents for collision-only nodes) and reimports when any were
        /// found; <paramref name="beforeReimport"/> runs first so the caller can re-arm a
        /// transient sidecar replay. Returns true when the importer changed.
        /// </summary>
        internal static bool RemoveDefaultMaterialRemaps(string fbxPath, Action beforeReimport)
        {
            var imp = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (imp == null) return false;
            var toRemove = new List<AssetImporter.SourceAssetIdentifier>();
            foreach (var kvp in imp.GetExternalObjectMap())
            {
                if (kvp.Key.type != typeof(Material)) continue;
                if (kvp.Key.name == "Lit" || kvp.Key.name == "No Name") toRemove.Add(kvp.Key);
            }
            if (toRemove.Count == 0) return false;
            beforeReimport?.Invoke();
            foreach (var key in toRemove) imp.RemoveRemap(key);
            imp.SaveAndReimport();
            return true;
        }
    }
}
