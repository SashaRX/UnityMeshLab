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
//
// The hub's full save also hands over a structure plan (generated LODs, sidecar
// collision, meshes with edited faces); FbxStructureWrite applies it to the same
// document after the channels, so one load and one save cover both.

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
            /// <summary>UV channels the write leaves alone, for telling coincident candidates apart.</summary>
            public readonly Vector2[][] signatureUvs = new Vector2[8][];
            public Color32[] colors32;
            public Color[] colors;
            /// <summary>The mesh stores colours as bytes: compare them as Color32, not as floats.</summary>
            public bool colorsAreBytes;
        }

        // What the tagged import says about each FBX corner of one mesh.
        internal sealed class Tagged
        {
            public int ordinal;
            public float[] cornerPositions;
            public readonly Vector2[][] uvs = new Vector2[8][];
            public Color32[] colors32;
            public Color[] colors;
            /// <summary>The FBX corner of each vertex of the import's triangles, submesh by submesh.</summary>
            public int[][] submeshCorners;
        }

        /// <summary>
        /// Writes the intent's channels of <paramref name="entries"/> into the FBX at
        /// <paramref name="sourceFbxPath"/> (or into a new file at <paramref name="outputFbxPath"/>),
        /// together with the new and replaced geometry of <paramref name="structure"/> when one
        /// is given, then reimports it and relinks <paramref name="sceneRoot"/>. Everything goes
        /// into one load and one save of the document: a refusal anywhere leaves the file as
        /// it was. Returns true when a file was written.
        /// </summary>
        internal static bool Write(string sourceFbxPath, IEnumerable<MeshEntry> entries, FbxExportIntent intent,
            string outputFbxPath, LODGroup sceneRoot, FbxStructurePlan structure = null)
        {
            string targetFbxPath = string.IsNullOrEmpty(outputFbxPath) ? sourceFbxPath : outputFbxPath;
            bool isVariant = !string.Equals(targetFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
            bool hasStructure = structure != null && !structure.IsEmpty;

            var importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            bool swapUv = importer != null && importer.swapUVChannels;
            bool generatedUv1 = importer != null && importer.generateSecondaryUV;
            // Meshes the structure step writes whole are not channel donors.
            var skip = hasStructure ? structure.WholeMeshNames() : null;
            var donors = CaptureDonors(entries, intent, generatedUv1, false, skip);
            // Writing UV1 switches 'Generate Lightmap UVs' off for the whole model, so every
            // mesh's generated UV1 goes into the file with it, edited or not.
            bool bakeUv1 = generatedUv1 && intent.IncludesUv(1) && donors.Any(d => d.uvs[1] != null);
            if (bakeUv1) donors = CaptureDonors(entries, intent, generatedUv1, true, skip);
            if (donors.Count == 0 && !hasStructure)
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
                int corners = WriteMesh(mesh, donor, tag, swapUv, channelsWritten);
                if (corners == 0) continue;
                if (!written.Add(tag.ordinal))
                    throw new InvalidOperationException($"FBX mesh '{mesh.GetName()}' is instanced by several Unity meshes; it cannot take edits from more than one.");
                meshesWritten++;
                cornersWritten += corners;
            }

            bool uv1Written = channelsWritten.Contains("UV1");
            // After the channels: new nodes take the UV sets their source has by now, and UV1
            // too when it is being baked for the whole model.
            var structureLog = new List<string>();
            int structural = hasStructure
                ? FbxStructureWrite.Apply(document, tagged, structure, swapUv, importer != null && importer.preserveHierarchy,
                    bakeUv1 && uv1Written ? 2 : 0, structureLog)
                : 0;

            if (meshesWritten == 0 && structural == 0)
            {
                UvtLog.Info($"[FBX Export] '{Path.GetFileName(sourceFbxPath)}': nothing changed; file left as is.");
                return false;
            }
            if (bakeUv1 && uv1Written) RequireEveryUv1(sourceFbxPath, donors, hasStructure ? structure.WholeMeshNames() : null, importedNames);

            // The sidecar's UV2 replay is held off for the import that follows only when that
            // import brings a UV1 written here; otherwise the sidecar still applies.
            if (uv1Written) Uv2AssetPostprocessor.fbxOverwritePaths.Add(targetFbxPath);
            try { FbxExport.ReplaceAtomically(targetFbxPath, temp => document.Save(Path.GetFullPath(temp))); }
            catch { Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath); throw; }
            if (!isVariant && uv1Written) KeepWrittenUv1(sourceFbxPath);
            string format = $"FBX {document.Major}.{document.Minor}, {(document.Binary ? "binary" : "ASCII")}";
            if (meshesWritten > 0)
                UvtLog.Info($"[FBX Export] {meshesWritten} mesh(es), {cornersWritten} corner value(s) of {string.Join(", ", channelsWritten.OrderBy(c => c))} -> {targetFbxPath} " +
                    $"({format}; polygons and other channels untouched)");
            foreach (string line in structureLog) UvtLog.Info($"[FBX Export] {line} -> {targetFbxPath} ({format})");

            // A save-as outside the project is a plain file; nothing to import.
            if (!targetFbxPath.StartsWith("Assets/", StringComparison.Ordinal) && !targetFbxPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath);
                return true;
            }
            AssetDatabase.ImportAsset(targetFbxPath, ImportAssetOptions.ForceUpdate);
            if (isVariant) ImportLikeSource(sourceFbxPath, targetFbxPath, uv1Written);
            else if (sceneRoot != null) FbxExport.RelinkSceneMeshReferences(sourceFbxPath, null, sceneRoot);
            return true;
        }

        // With generation switched off, a mesh whose UV1 is not in the file has none: refuse
        // rather than drop the lightmap UVs of meshes the save did not see.
        static void RequireEveryUv1(string sourceFbxPath, List<Donor> donors, ICollection<string> wholeMeshes, HashSet<string> importedNames)
        {
            var withUv1 = new HashSet<string>(donors.Where(d => d.uvs[1] != null).Select(d => d.name), StringComparer.Ordinal);
            if (wholeMeshes != null) withUv1.UnionWith(wholeMeshes);
            var lost = importedNames.Where(n => !withUv1.Contains(n) && !MeshNaming.IsCollision(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (lost.Count == 0) return;
            throw new InvalidOperationException(
                $"'Generate Lightmap UVs' is on for '{sourceFbxPath}'. Writing UV1 switches it off for the whole model, and " +
                $"{string.Join(", ", lost.Select(n => $"'{n}'"))} would lose the lightmap UVs Unity generates for them (they are not loaded here). " +
                "Load the whole model, or switch the setting off first; nothing was written.");
        }

        // ── Donors ──

        // Unity UV channel ↔ FBX UV set: 'Swap UVs' on the importer exchanges the first two.
        internal static int FbxUvSet(int unityChannel, bool swapUv) => swapUv && unityChannel < 2 ? 1 - unityChannel : unityChannel;

        static List<Donor> CaptureDonors(IEnumerable<MeshEntry> entries, FbxExportIntent intent, bool generatedUv1, bool bakeUv1, ICollection<string> skip)
        {
            var donors = new List<Donor>();
            var byName = new Dictionary<string, Donor>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e != null && e.include))
            {
                var donor = CaptureDonor(entry, intent, generatedUv1, bakeUv1);
                if (donor == null || (skip != null && skip.Contains(donor.name))) continue;
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

        // The entry's working mesh with the intent's channels it edited, or null when it edited
        // none. With bakeUv1, its UV1 counts as edited whatever it holds.
        static Donor CaptureDonor(MeshEntry entry, FbxExportIntent intent, bool generatedUv1, bool bakeUv1)
        {
            var source = entry.originalMesh ?? entry.fbxMesh;
            var mesh = entry.repackedMesh ?? entry.transferredMesh ?? source;
            var identity = entry.fbxMesh ?? source;
            if (mesh == null || identity == null) return null;

            var donor = new Donor { name = identity.name };
            // A channel the working copy holds exactly as the current import does was not
            // edited here (e.g. lightmap UVs Unity generates on import): leave it out. A
            // mesh edited in place (the working copy is the asset mesh) cannot be told
            // apart, so its channels go on to the per-corner comparison.
            var imported = entry.fbxMesh != null && entry.fbxMesh != mesh && entry.fbxMesh.isReadable
                && entry.fbxMesh.vertexCount == mesh.vertexCount ? entry.fbxMesh : null;
            // The asset mesh's UV1 is Unity's own when 'Generate Lightmap UVs' is on: not an
            // edit, and no signature either (the file holds something else there).
            bool skipUv1 = generatedUv1 && mesh == entry.fbxMesh && !bakeUv1;
            bool any = CaptureUvs(donor, mesh, imported, intent, skipUv1, generatedUv1, bakeUv1);
            if ((intent & FbxExportIntent.VertexColors) != 0) any |= CaptureColors(donor, mesh, imported);
            if (!any) return null;

            var vertices = mesh.vertices;
            donor.positions = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                donor.positions[i * 3] = vertices[i].x; donor.positions[i * 3 + 1] = vertices[i].y; donor.positions[i * 3 + 2] = vertices[i].z;
            }
            ReadFaces(mesh, out donor.faces, out donor.faceSizes);
            return donor;
        }

        static bool CaptureUvs(Donor donor, Mesh mesh, Mesh imported, FbxExportIntent intent, bool skipUv1, bool generatedUv1, bool bakeUv1)
        {
            bool any = false;
            var list = new List<Vector2>();
            var importedList = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                mesh.GetUVs(ch, list);
                if (list.Count != mesh.vertexCount) continue;
                if (!intent.IncludesUv(ch) || (ch == 1 && skipUv1))
                {
                    if (ch != 1 || !generatedUv1) donor.signatureUvs[ch] = list.ToArray();
                    continue;
                }
                if (imported != null && !(ch == 1 && bakeUv1))
                {
                    imported.GetUVs(ch, importedList);
                    if (importedList.SequenceEqual(list))
                    {
                        if (ch != 1 || !generatedUv1) donor.signatureUvs[ch] = list.ToArray();
                        continue;
                    }
                }
                donor.uvs[ch] = list.ToArray();
                any = true;
            }
            return any;
        }

        static bool CaptureColors(Donor donor, Mesh mesh, Mesh imported)
        {
            var colors = mesh.colors;
            if (colors == null || colors.Length != mesh.vertexCount) return false;
            bool bytes = mesh.HasVertexAttribute(VertexAttribute.Color)
                && mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8;
            if (imported != null && (bytes
                ? imported.colors32.SequenceEqual(mesh.colors32, Color32Comparer.Instance)
                : imported.colors.SequenceEqual(colors)))
                return false;
            donor.colors = colors;
            donor.colors32 = mesh.colors32;
            donor.colorsAreBytes = bytes;
            return true;
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
            foreach (var t in tags) corners = Math.Max(corners, Mathf.RoundToInt(t.x) + 1);
            var tag = new Tagged { ordinal = ordinal, cornerPositions = new float[corners * 3] };
            for (int i = 0; i < tag.cornerPositions.Length; i++) tag.cornerPositions[i] = float.NaN;

            var vertices = mesh.vertices;
            var cornerOf = new int[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                int c = cornerOf[v] = Mathf.RoundToInt(tags[v].x);
                tag.cornerPositions[c * 3] = vertices[v].x; tag.cornerPositions[c * 3 + 1] = vertices[v].y; tag.cornerPositions[c * 3 + 2] = vertices[v].z;
            }
            tag.submeshCorners = new int[mesh.subMeshCount][];
            for (int s = 0; s < mesh.subMeshCount; s++)
                tag.submeshCorners[s] = mesh.GetTopology(s) == MeshTopology.Triangles
                    ? Array.ConvertAll(mesh.GetIndices(s), i => cornerOf[i]) : new int[0];
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
        static int WriteMesh(Autodesk.Fbx.FbxMesh mesh, Donor donor, Tagged tag, bool swapUv, HashSet<string> channelsWritten)
        {
            var topology = new FbxLayerChannels.Topology(mesh);
            var cornerToVertex = PairCorners(donor, tag, topology);

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
                int uvCorners = WriteChannel(donor.name, $"UV{ch}", topology, cornerToVertex, !exists, 2,
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
                int colorCorners = WriteChannel(donor.name, "vertex colours", topology, cornerToVertex, !exists, 4,
                    (c, v) => comparable && (donor.colorsAreBytes ? Same(tag.colors32[c], donor.colors32[v]) : tag.colors[c].Equals(donor.colors[v])),
                    (v, values, at) => { var col = donor.colors[v]; values[at] = col.r; values[at + 1] = col.g; values[at + 2] = col.b; values[at + 3] = col.a; },
                    (values, changed) => FbxLayerChannels.WriteColor(mesh, topology, values, changed));
                if (colorCorners > 0) channelsWritten.Add("vertex colours");
                written += colorCorners;
            }
            return written;
        }

        // Every FBX corner's vertex in the working mesh; refuses what the file cannot hold.
        static int[] PairCorners(Donor donor, Tagged tag, in FbxLayerChannels.Topology topology)
        {
            var match = FbxCornerMatch.Match(topology.polygonSizes, CornerPositions(donor, tag, topology), donor.positions, donor.faces, donor.faceSizes,
                (c, v) => SignatureMatches(donor, tag, c, v),
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
            return match.cornerToVertex;
        }

        // The tagged corner positions, one per FBX corner; trailing corners of degenerate
        // polygons have no vertex in the import and stay NaN.
        static float[] CornerPositions(Donor donor, Tagged tag, in FbxLayerChannels.Topology topology)
        {
            int length = topology.CornerCount * 3;
            if (tag.cornerPositions.Length > length)
                throw new InvalidOperationException($"'{donor.name}': the tagged import does not match the FBX polygons.");
            if (tag.cornerPositions.Length == length) return tag.cornerPositions;
            var padded = new float[length];
            for (int i = 0; i < length; i++) padded[i] = i < tag.cornerPositions.Length ? tag.cornerPositions[i] : float.NaN;
            return padded;
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

            if (newSet) count += FillFromControlPoints(meshName, channel, topology, values, changed, arity);
            write(values, changed);
            UvtLog.Verbose($"[FBX Export] '{meshName}': {channel} — {count}/{corners} corner(s) written.");
            return count;
        }

        // A new set needs a value at every corner; an unpaired corner takes the value of a
        // paired corner on the same control point, never an invented one. Returns the corners filled.
        static int FillFromControlPoints(string meshName, string channel, in FbxLayerChannels.Topology topology, double[] values, bool[] changed, int arity)
        {
            var byPoint = new Dictionary<int, int>();
            for (int c = 0; c < changed.Length; c++) if (changed[c]) byPoint[topology.cornerControlPoint[c]] = c;
            int filled = 0;
            for (int c = 0; c < changed.Length; c++)
            {
                if (changed[c]) continue;
                if (!byPoint.TryGetValue(topology.cornerControlPoint[c], out int from))
                    throw new InvalidOperationException($"'{meshName}': {channel} would be a new set, but corner {c} has no matching vertex to take a value from; nothing was written.");
                Array.Copy(values, from * arity, values, c * arity, arity);
                changed[c] = true;
                filled++;
            }
            return filled;
        }

        // Attributes the write leaves alone must agree between the corner and the vertex.
        static bool SignatureMatches(Donor donor, Tagged tag, int corner, int vertex)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (donor.signatureUvs[ch] == null || tag.uvs[ch] == null) continue;
                if (!donor.signatureUvs[ch][vertex].Equals(tag.uvs[ch][corner])) return false;
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
            // The variant's UV1 is the one just written: Unity must not regenerate it, and a
            // sidecar at the variant's path must not replay over it.
            if (uv1Written)
            {
                variant.generateSecondaryUV = false;
                Uv2AssetPostprocessor.fbxOverwritePaths.Add(variantFbxPath);
            }
            variant.SaveAndReimport();
        }
    }
}
#endif
