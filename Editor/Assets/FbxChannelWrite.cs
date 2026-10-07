// FbxChannelWrite.cs — the strict FBX re-save for per-vertex channels (UV sets, vertex
// colours). The FBX document is edited through the FBX SDK: only the layer elements of
// the channels the tool changed are touched, and within them only the corners whose
// value changed. Polygons (quads, n-gons), control points, normals, smoothing, the
// other UV and colour sets, materials, nodes, the file format and version come
// through as the source file has them. Unity's FBX Exporter is not involved, so
// nothing is triangulated, welded, re-packed or quantised.
//
// Unity numbers a mesh's vertices after splitting, welding and triangulating the FBX
// polygons; which FBX corner a vertex belongs to is recovered from a throwaway import
// of a tagged copy of the file (each corner's index stored in an extra UV set), whose
// vertices are bit-identical in position to the working meshes. FbxCornerMatch then
// pairs every corner with the working mesh's vertex. A corner that cannot be paired
// keeps its stored value; a write that would have to invent values is refused.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxChannelWrite
    {
        internal const FbxExportIntent Supported = FbxExportIntent.AnyUv | FbxExportIntent.VertexColors;
        const string TempFolder = "Assets/__MeshLabTemp";

        /// <summary>True when every bit of <paramref name="intent"/> is a channel this path writes.</summary>
        internal static bool Handles(FbxExportIntent intent) => intent != FbxExportIntent.None && (intent & ~Supported) == 0;

        // The working mesh's data, read before anything is imported.
        sealed class Donor
        {
            public string name;
            public float[] positions;
            public int[] faces, faceSizes;
            public readonly Vector2[][] uvs = new Vector2[8][];
            public Color32[] colors32;
            public Color[] colors;
            /// <summary>The mesh stores colours as bytes: compare them as Color32, not as floats.</summary>
            public bool colorsAreBytes;
        }

        // What the tagged import says about each FBX corner of one mesh.
        sealed class Tagged
        {
            public int ordinal;
            public float[] cornerPositions;
            public readonly Vector2[][] uvs = new Vector2[8][];
            public Color32[] colors32;
            public Color[] colors;
        }

        /// <summary>
        /// Writes the intent's channels of <paramref name="entries"/> into the FBX at
        /// <paramref name="sourceFbxPath"/> (or into a new file at <paramref name="outputFbxPath"/>),
        /// then reimports it and relinks <paramref name="sceneRoot"/>. Returns true when a file was written.
        /// </summary>
        internal static bool Write(string sourceFbxPath, IEnumerable<MeshEntry> entries, FbxExportIntent intent,
            string outputFbxPath, LODGroup sceneRoot)
        {
            string targetFbxPath = string.IsNullOrEmpty(outputFbxPath) ? sourceFbxPath : outputFbxPath;
            bool isVariant = !string.Equals(targetFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);

            var importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            bool swapUv = importer != null && importer.swapUVChannels;
            bool generatedUv1 = importer != null && importer.generateSecondaryUV;
            var donors = CaptureDonors(entries, intent, generatedUv1);
            if (donors.Count == 0)
            {
                UvtLog.Warn($"[FBX Export] No meshes carry data for {intent}.");
                return false;
            }

            using var document = FbxSourceDocument.Load(Path.GetFullPath(sourceFbxPath));
            var tagged = ImportTagged(sourceFbxPath, swapUv, out var importedNames);

            int meshesWritten = 0, cornersWritten = 0;
            var written = new HashSet<int>();
            var channelsWritten = new HashSet<string>();
            foreach (var donor in donors)
            {
                if (!tagged.TryGetValue(donor.name, out var tag))
                {
                    if (importedNames.Contains(donor.name))
                        throw new InvalidOperationException(
                            $"'{donor.name}': the corner tags did not survive the import (is Mesh Compression on for '{sourceFbxPath}'? " +
                            "The exact corner pairing needs it Off); nothing was written.");
                    UvtLog.Warn($"[FBX Export] '{donor.name}' is not a mesh of '{sourceFbxPath}'; skipped.");
                    continue;
                }
                var mesh = document.Meshes[tag.ordinal];
                int corners = WriteMesh(mesh, donor, tag, intent, swapUv, channelsWritten);
                if (corners == 0) continue;
                if (!written.Add(tag.ordinal))
                    throw new InvalidOperationException($"FBX mesh '{mesh.GetName()}' is instanced by several Unity meshes; it cannot take edits from more than one.");
                meshesWritten++;
                cornersWritten += corners;
            }

            if (meshesWritten == 0)
            {
                UvtLog.Info($"[FBX Export] '{Path.GetFileName(sourceFbxPath)}': {intent} unchanged; file left as is.");
                return false;
            }

            Uv2AssetPostprocessor.fbxOverwritePaths.Add(targetFbxPath);
            try { FbxExport.ReplaceAtomically(targetFbxPath, temp => document.Save(Path.GetFullPath(temp))); }
            catch { Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath); throw; }
            if (!isVariant && channelsWritten.Contains("UV1")) KeepWrittenUv1(sourceFbxPath);
            UvtLog.Info($"[FBX Export] {meshesWritten} mesh(es), {cornersWritten} corner value(s) of {string.Join(", ", channelsWritten.OrderBy(c => c))} -> {targetFbxPath} " +
                $"(FBX {document.Major}.{document.Minor}, {(document.Binary ? "binary" : "ASCII")}; polygons and other channels untouched)");

            // A save-as outside the project is a plain file; nothing to import.
            if (!targetFbxPath.StartsWith("Assets/", StringComparison.Ordinal) && !targetFbxPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath);
                return true;
            }
            AssetDatabase.ImportAsset(targetFbxPath, ImportAssetOptions.ForceUpdate);
            if (isVariant) ImportLikeSource(sourceFbxPath, targetFbxPath, channelsWritten.Contains("UV1"));
            else if (sceneRoot != null) FbxExport.RelinkSceneMeshReferences(sourceFbxPath, null, sceneRoot);
            return true;
        }

        // ── Donors ──

        // Unity UV channel ↔ FBX UV set: 'Swap UVs' on the importer exchanges the first two.
        static int FbxUvSet(int unityChannel, bool swapUv) => swapUv && unityChannel < 2 ? 1 - unityChannel : unityChannel;

        static List<Donor> CaptureDonors(IEnumerable<MeshEntry> entries, FbxExportIntent intent, bool generatedUv1)
        {
            var donors = new List<Donor>();
            var byName = new Dictionary<string, Donor>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || !entry.include) continue;
                var source = entry.originalMesh ?? entry.fbxMesh;
                var mesh = entry.repackedMesh ?? entry.transferredMesh ?? source;
                var identity = entry.fbxMesh ?? source;
                if (mesh == null || identity == null) continue;

                var donor = new Donor { name = identity.name };
                var vertices = mesh.vertices;
                donor.positions = new float[vertices.Length * 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    donor.positions[i * 3] = vertices[i].x; donor.positions[i * 3 + 1] = vertices[i].y; donor.positions[i * 3 + 2] = vertices[i].z;
                }
                ReadFaces(mesh, out donor.faces, out donor.faceSizes);

                // A channel the working copy holds exactly as the current import does was not
                // edited here (e.g. lightmap UVs Unity generates on import): leave it out. A
                // mesh edited in place (the working copy is the asset mesh) cannot be told
                // apart, so its channels go on to the per-corner comparison.
                var imported = entry.fbxMesh != null && entry.fbxMesh != mesh && entry.fbxMesh.isReadable
                    && entry.fbxMesh.vertexCount == mesh.vertexCount ? entry.fbxMesh : null;
                bool any = false;
                var list = new List<Vector2>();
                var importedList = new List<Vector2>();
                for (int ch = 0; ch < 8; ch++)
                {
                    if (!intent.IncludesUv(ch)) continue;
                    // The asset mesh's UV1 is Unity's own when 'Generate Lightmap UVs' is on.
                    if (ch == 1 && generatedUv1 && mesh == entry.fbxMesh) continue;
                    mesh.GetUVs(ch, list);
                    if (list.Count != mesh.vertexCount) continue;
                    if (imported != null) { imported.GetUVs(ch, importedList); if (importedList.SequenceEqual(list)) continue; }
                    donor.uvs[ch] = list.ToArray();
                    any = true;
                }
                if ((intent & FbxExportIntent.VertexColors) != 0)
                {
                    var c = mesh.colors;
                    bool bytes = mesh.HasVertexAttribute(VertexAttribute.Color)
                        && mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;
                    bool unedited = imported != null && (bytes
                        ? imported.colors32.SequenceEqual(mesh.colors32, Color32Comparer.Instance)
                        : imported.colors.SequenceEqual(c));
                    if (c != null && c.Length == mesh.vertexCount && !unedited)
                    {
                        donor.colors = c;
                        donor.colors32 = mesh.colors32;
                        donor.colorsAreBytes = bytes;
                        any = true;
                    }
                }
                if (!any) continue;
                // Instances of one FBX mesh share its data: the same edit twice is one edit,
                // different edits cannot both be written.
                if (byName.TryGetValue(donor.name, out var first))
                {
                    if (!SameEdit(first, donor))
                        throw new InvalidOperationException($"'{donor.name}' is instanced and its instances were edited differently; they share one FBX mesh, so only one edit can be written. Nothing was written.");
                    continue;
                }
                byName[donor.name] = donor;
                donors.Add(donor);
            }
            return donors;
        }

        static bool SameEdit(Donor a, Donor b)
        {
            if (!a.positions.SequenceEqual(b.positions) || !a.faces.SequenceEqual(b.faces)) return false;
            for (int ch = 0; ch < 8; ch++)
                if ((a.uvs[ch] == null) != (b.uvs[ch] == null) || (a.uvs[ch] != null && !a.uvs[ch].SequenceEqual(b.uvs[ch]))) return false;
            return (a.colors == null) == (b.colors == null) && (a.colors == null || a.colors.SequenceEqual(b.colors));
        }

        static void ReadFaces(Mesh mesh, out int[] faces, out int[] faceSizes)
        {
            var indices = new List<int>();
            var sizes = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int size;
                switch (mesh.GetTopology(s))
                {
                    case MeshTopology.Triangles: size = 3; break;
                    case MeshTopology.Quads: size = 4; break;
                    default: continue; // lines and points have no corners to match
                }
                var sub = mesh.GetIndices(s);
                indices.AddRange(sub);
                for (int i = 0; i < sub.Length / size; i++) sizes.Add(size);
            }
            faces = indices.ToArray();
            faceSizes = sizes.ToArray();
        }

        // ── Tagged import ──

        static Dictionary<string, Tagged> ImportTagged(string sourceFbxPath, bool swapUv, out HashSet<string> importedNames)
        {
            importedNames = new HashSet<string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            string tempPath = $"{TempFolder}/corner_tags_{Guid.NewGuid():N}.fbx";
            var tagChannels = new Dictionary<int, int>();
            using (var tagDocument = FbxSourceDocument.Load(Path.GetFullPath(sourceFbxPath)))
            {
                for (int i = 0; i < tagDocument.Meshes.Count; i++)
                    tagChannels[i] = FbxUvSet(FbxLayerChannels.AddCornerTag(tagDocument.Meshes[i], i), swapUv);
                tagDocument.Save(Path.GetFullPath(tempPath));
            }

            Uv2AssetPostprocessor.bypassPaths.Add(tempPath);
            try
            {
                AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(tempPath) as ModelImporter;
                var source = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                if (importer == null) throw new InvalidOperationException("The tagged copy did not import as a model.");
                // Same settings as the source so vertices land where the working meshes'
                // are; nothing else is needed from this import.
                if (source != null) new Preset(source).ApplyTo(importer);
                importer.isReadable = true;
                importer.generateSecondaryUV = false;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                Uv2AssetPostprocessor.bypassPaths.Add(tempPath);
                importer.SaveAndReimport();

                var tagged = new Dictionary<string, Tagged>(StringComparer.Ordinal);
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<Mesh>())
                {
                    importedNames.Add(mesh.name);
                    var tag = ReadTags(mesh, tagChannels);
                    if (tag != null && !tagged.ContainsKey(mesh.name)) tagged[mesh.name] = tag;
                }
                return tagged;
            }
            finally
            {
                Uv2AssetPostprocessor.bypassPaths.Remove(tempPath);
                AssetDatabase.DeleteAsset(tempPath);
                if (AssetDatabase.IsValidFolder(TempFolder) && AssetDatabase.FindAssets(string.Empty, new[] { TempFolder }).Length == 0)
                    AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        static Tagged ReadTags(Mesh mesh, Dictionary<int, int> tagChannels)
        {
            var list = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                mesh.GetUVs(ch, list);
                if (list.Count == 0 || list.Count != mesh.vertexCount) continue;
                int ordinal = TagOrdinal(list);
                if (ordinal < 0 || !tagChannels.TryGetValue(ordinal, out int expected) || expected != ch) continue;
                return BuildTagged(mesh, ch, ordinal, list);
            }
            return null;
        }

        // The ordinal every vertex of the channel agrees on, or -1 when it is not a tag channel.
        static int TagOrdinal(List<Vector2> tags)
        {
            float v = tags[0].y;
            if (v >= 0 || !IsWhole(v, out int tagValue)) return -1;
            foreach (var t in tags)
                if (!IsWhole(t.y, out int y) || y != tagValue || t.x < 0 || !IsWhole(t.x, out _)) return -1;
            return -tagValue - 1;
        }

        // Tag values are whole numbers below 2^24, so any fractional part means "not a tag".
        static bool IsWhole(float f, out int whole)
        {
            whole = Mathf.RoundToInt(f);
            return Mathf.Abs(f - whole) < 1e-3f;
        }

        static Tagged BuildTagged(Mesh mesh, int tagChannel, int ordinal, List<Vector2> tags)
        {
            int corners = 0;
            foreach (var t in tags) corners = Math.Max(corners, (int)t.x + 1);
            var tag = new Tagged { ordinal = ordinal, cornerPositions = new float[corners * 3] };
            for (int i = 0; i < tag.cornerPositions.Length; i++) tag.cornerPositions[i] = float.NaN;

            var vertices = mesh.vertices;
            var cornerOf = new int[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                int c = cornerOf[v] = (int)tags[v].x;
                tag.cornerPositions[c * 3] = vertices[v].x; tag.cornerPositions[c * 3 + 1] = vertices[v].y; tag.cornerPositions[c * 3 + 2] = vertices[v].z;
            }
            var list = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == tagChannel) continue;
                mesh.GetUVs(ch, list);
                if (list.Count != vertices.Length) continue;
                tag.uvs[ch] = new Vector2[corners];
                for (int v = 0; v < vertices.Length; v++) tag.uvs[ch][cornerOf[v]] = list[v];
            }
            var colors = mesh.colors;
            if (colors != null && colors.Length == vertices.Length)
            {
                var colors32 = mesh.colors32;
                tag.colors = new Color[corners];
                tag.colors32 = new Color32[corners];
                for (int v = 0; v < vertices.Length; v++) { tag.colors[cornerOf[v]] = colors[v]; tag.colors32[cornerOf[v]] = colors32[v]; }
            }
            return tag;
        }

        // ── Per mesh ──

        // Returns the number of corner values written into the document.
        static int WriteMesh(Autodesk.Fbx.FbxMesh mesh, Donor donor, Tagged tag, FbxExportIntent intent, bool swapUv, HashSet<string> channelsWritten)
        {
            var topology = new FbxLayerChannels.Topology(mesh);
            if (tag.cornerPositions.Length > topology.CornerCount * 3)
                throw new InvalidOperationException($"'{donor.name}': the tagged import does not match the FBX polygons.");
            var cornerPositions = tag.cornerPositions;
            if (cornerPositions.Length < topology.CornerCount * 3)
            {
                // Trailing corners of degenerate polygons have no vertex in the import.
                var padded = new float[topology.CornerCount * 3];
                for (int i = 0; i < padded.Length; i++) padded[i] = i < cornerPositions.Length ? cornerPositions[i] : float.NaN;
                cornerPositions = padded;
            }

            var match = FbxCornerMatch.Match(topology.polygonSizes, cornerPositions, donor.positions, donor.faces, donor.faceSizes,
                (c, v) => SignatureMatches(donor, tag, intent, c, v),
                (a, b) => SameWrittenValues(donor, a, b));
            if (match.conflicts > 0)
                throw new InvalidOperationException(
                    $"'{donor.name}': {match.conflicts} FBX corner(s) get different values from different triangles of the same polygon " +
                    "(a UV seam or colour edge runs inside a polygon). The FBX polygon cannot hold that without being split; nothing was written.");
            if (match.unresolved > 0)
                throw new InvalidOperationException(
                    $"'{donor.name}': {match.unresolved} FBX corner(s) have no face in the working mesh — its geometry differs from the file " +
                    "(simplified or edited faces). Channels alone cannot carry that; save with a rebuild. Nothing was written.");
            if (match.missing > 0)
                UvtLog.Warn($"[FBX Export] '{donor.name}': {match.missing} corner(s) of degenerate polygons have no vertex in Unity's import; they keep their stored values.");

            int written = 0;
            int existingUvSets = FbxLayerChannels.UvElements(mesh).Count;
            // In FBX set order: with 'Swap UVs', Unity UV0 is set 1, and set 0 must exist first.
            foreach (int ch in Enumerable.Range(0, 8).OrderBy(c => FbxUvSet(c, swapUv)))
            {
                if (donor.uvs[ch] == null) continue;
                int set = FbxUvSet(ch, swapUv);
                bool exists = set < existingUvSets;
                var stored = exists ? tag.uvs[ch] : null;
                if (exists && stored == null)
                    UvtLog.Verbose($"[FBX Export] '{donor.name}': UV{ch} cannot be compared with the stored set (the tagged copy used that channel); it is written whole.");
                int uvCorners = WriteChannel(donor.name, $"UV{ch}", topology, match.cornerToVertex, !exists, 2,
                    (c, v) => stored != null && stored[c].Equals(donor.uvs[ch][v]),
                    (v, values, at) => { values[at] = donor.uvs[ch][v].x; values[at + 1] = donor.uvs[ch][v].y; },
                    (values, changed) => FbxLayerChannels.WriteUv(mesh, set, topology, values, changed));
                if (uvCorners > 0) channelsWritten.Add($"UV{ch}");
                written += uvCorners;
            }
            if (donor.colors != null)
            {
                bool exists = FbxLayerChannels.ColorElement(mesh) != null;
                bool comparable = exists && tag.colors != null;
                int colorCorners = WriteChannel(donor.name, "vertex colours", topology, match.cornerToVertex, !exists, 4,
                    (c, v) => comparable && (donor.colorsAreBytes ? Same(tag.colors32[c], donor.colors32[v]) : tag.colors[c].Equals(donor.colors[v])),
                    (v, values, at) => { var col = donor.colors[v]; values[at] = col.r; values[at + 1] = col.g; values[at + 2] = col.b; values[at + 3] = col.a; },
                    (values, changed) => FbxLayerChannels.WriteColor(mesh, topology, values, changed));
                if (colorCorners > 0) channelsWritten.Add("vertex colours");
                written += colorCorners;
            }
            return written;
        }

        static int WriteChannel(string meshName, string channel, in FbxLayerChannels.Topology topology, int[] cornerToVertex, bool newSet, int arity,
            Func<int, int, bool> unchanged, Action<int, double[], int> fill, Func<double[], bool[], int> write)
        {
            int corners = topology.CornerCount;
            var values = new double[corners * arity];
            var changed = new bool[corners];
            int count = 0;
            for (int c = 0; c < corners; c++)
            {
                int v = cornerToVertex[c];
                if (v < 0 || unchanged(c, v)) continue;
                fill(v, values, c * arity);
                changed[c] = true;
                count++;
            }
            if (count == 0) return 0;

            if (newSet)
            {
                // A new set needs a value at every corner; an unpaired corner takes the value
                // of a paired corner on the same control point, never an invented one.
                var byPoint = new Dictionary<int, int>();
                for (int c = 0; c < corners; c++) if (changed[c]) byPoint[topology.cornerControlPoint[c]] = c;
                for (int c = 0; c < corners; c++)
                {
                    if (changed[c]) continue;
                    if (!byPoint.TryGetValue(topology.cornerControlPoint[c], out int from))
                        throw new InvalidOperationException($"'{meshName}': {channel} would be a new set, but corner {c} has no matching vertex to take a value from; nothing was written.");
                    Array.Copy(values, from * arity, values, c * arity, arity);
                    changed[c] = true;
                    count++;
                }
            }
            write(values, changed);
            UvtLog.Verbose($"[FBX Export] '{meshName}': {channel} — {count}/{corners} corner(s) written.");
            return count;
        }

        // Attributes the write leaves alone must agree between the corner and the vertex.
        static bool SignatureMatches(Donor donor, Tagged tag, FbxExportIntent intent, int corner, int vertex)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (intent.IncludesUv(ch) || tag.uvs[ch] == null) continue;
                if (donor.uvs[ch] != null && !donor.uvs[ch][vertex].Equals(tag.uvs[ch][corner])) return false;
            }
            return true;
        }

        static bool SameWrittenValues(Donor donor, int a, int b)
        {
            for (int ch = 0; ch < 8; ch++)
                if (donor.uvs[ch] != null && !donor.uvs[ch][a].Equals(donor.uvs[ch][b])) return false;
            return donor.colors == null || donor.colors[a].Equals(donor.colors[b]);
        }

        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        sealed class Color32Comparer : IEqualityComparer<Color32>
        {
            public static readonly Color32Comparer Instance = new Color32Comparer();
            public bool Equals(Color32 a, Color32 b) => Same(a, b);
            public int GetHashCode(Color32 c) => (c.r << 24) | (c.g << 16) | (c.b << 8) | c.a;
        }

        // ── Importers ──

        // generateSecondaryUV regenerates Unity UV channel 1 on import and would replace the
        // channel just written; switch it off for this model (the one importer setting the
        // write needs).
        static void KeepWrittenUv1(string sourceFbxPath)
        {
            var importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            if (importer == null || !importer.generateSecondaryUV) return;
            importer.generateSecondaryUV = false;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(sourceFbxPath);
            UvtLog.Info($"[FBX Export] 'Generate Lightmap UVs' switched off on '{sourceFbxPath}' so the written UV1 is used.");
        }

        // A variant is a new file: import it the way its source is imported, so its meshes
        // carry the same names and layout as the source's — except a written UV1 is kept.
        static void ImportLikeSource(string sourceFbxPath, string variantFbxPath, bool uv1Written)
        {
            var source = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            var variant = AssetImporter.GetAtPath(variantFbxPath) as ModelImporter;
            if (source == null || variant == null) return;
            new Preset(source).ApplyTo(variant);
            // The variant's UV1 is the one just written; Unity must not regenerate it.
            if (uv1Written) variant.generateSecondaryUV = false;
            variant.SaveAndReimport();
        }
    }
}
#endif
