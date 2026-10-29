// FbxChannelWrite.cs — the strict FBX re-save for per-vertex channels (UV sets, vertex
// colours, and for the hub's save normals and tangents). The FBX document is edited
// through the FBX SDK: only the layer elements of the channels the tool changed are
// touched, and within them only the corners whose value changed. A polygon with a new
// internal seam is split using the import's triangles. Other polygons, control points,
// smoothing, the other channels, materials and nodes come through from the source.
// Nearby UV seams receive separate control-point identities so Unity's importer
// retains their distinct values; positions and polygon corners do not move.
// Output format and embedding follow project settings;
// the source file version is preserved, subject to the SDK's supported versions.
// Unity's FBX Exporter is not involved, so nothing is welded, re-packed
// or quantised. Normals and
// tangents are directions in the mesh's space, so they go back through the map fitted
// to the mesh's import (FbxSpaceFit).
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
        /// <summary>UV sets and vertex colours: what "Save channels only" writes.</summary>
        internal const FbxExportIntent ChannelsOnly = FbxExportIntent.AnyUv | FbxExportIntent.VertexColors;
        /// <summary>Everything this path writes: the channels, plus normals and tangents through the mesh's fit.</summary>
        internal const FbxExportIntent Supported = ChannelsOnly | FbxExportIntent.Normals | FbxExportIntent.Tangents;
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
            /// <summary>Colours the save does not write, compared like <see cref="signatureUvs"/>: bytes for an 8-bit stream, floats otherwise.</summary>
            public Color32[] signatureColors32;
            public Color[] signatureColors;
            /// <summary>Channels the import has and the working mesh no longer does (Cleanup removed them).</summary>
            public readonly bool[] removedUvs = new bool[8];
            /// <summary>UV channels the import has; a channel it lacks was added after the import.</summary>
            public readonly bool[] importedUvs = new bool[8];
            public bool removedColors, removedNormals, removedTangents;
            /// <summary>Unity-space normals the save writes; with <see cref="tangents"/>, the normals the binormals are built from.</summary>
            public Vector3[] normals, frameNormals;
            public Vector4[] tangents;
            /// <summary>The tangents are taken only because the normals moved: a file without a frame keeps none.</summary>
            public bool tangentsFollowNormals;
            /// <summary>
            /// A normals-only save: the file's tangents stay, and only their binormals are rebuilt
            /// on the new normals.
            /// </summary>
            public bool keepFileTangents;
            public bool[] shadingSubmeshes, selectedCorners;
            public int[] cornerSubmeshes;
            public HashSet<int>[] submeshVertices;
            public bool HasValues => colors != null || normals != null || tangents != null || uvs.Any(uv => uv != null);
            public Color32[] colors32;
            public Color[] colors;
            /// <summary>The mesh stores colours as bytes: compare them as Color32, not as floats.</summary>
            public bool colorsAreBytes;
        }

        // What the tagged import says about each FBX corner of one mesh.
        internal sealed class Tagged
        {
            public int ordinal;
            /// <summary>Another FBX mesh imported under the same name: a lookup by name may mean either.</summary>
            public bool ambiguous;
            public float[] cornerPositions;
            public readonly Vector2[][] uvs = new Vector2[8][];
            public Color32[] colors32;
            public Color[] colors;
            public Vector3[] normals;
            public Vector4[] tangents;
            /// <summary>The FBX corner of each vertex of the import's triangles, submesh by submesh.</summary>
            public int[][] submeshCorners;
            /// <summary>Corners per face of each submesh: 3, or 4 for a Keep Quads submesh.</summary>
            public int[] submeshFaceSizes;

            /// <summary>The import's faces as triangles (quads split along their first diagonal), as FBX corners.</summary>
            public int[] TriangleCorners()
            {
                var triangles = new List<int>();
                for (int s = 0; s < submeshCorners.Length; s++)
                {
                    var corners = submeshCorners[s];
                    if (submeshFaceSizes[s] != 4) { triangles.AddRange(corners); continue; }
                    for (int f = 0; f + 3 < corners.Length; f += 4)
                        triangles.AddRange(new[] { corners[f], corners[f + 1], corners[f + 2], corners[f], corners[f + 2], corners[f + 3] });
                }
                return triangles.ToArray();
            }
        }

        /// <summary>
        /// Writes the intent's channels of <paramref name="entries"/> into the FBX at
        /// <paramref name="sourceFbxPath"/> (or into a new file at <paramref name="outputFbxPath"/>),
        /// together with the new and replaced geometry of <paramref name="structure"/> when one
        /// is given, then reimports it and relinks <paramref name="sceneRoot"/>. Everything goes
        /// into one load and one save of the document: a refusal anywhere leaves the file as
        /// it was. Validation-only mode checks the disposable document without saving or importing the output.
        /// Returns true when validation succeeded or a file was written with the required import settings.
        /// </summary>
        internal static bool Write(string sourceFbxPath, IEnumerable<MeshEntry> entries, FbxExportIntent intent,
            string outputFbxPath, LODGroup sceneRoot, FbxStructurePlan structure = null, FbxExport.ShadingSelection shadingSelection = null, bool validationOnly = false)
        {
            string targetFbxPath = string.IsNullOrEmpty(outputFbxPath) ? sourceFbxPath : outputFbxPath;
            bool isVariant = !string.Equals(targetFbxPath, sourceFbxPath, StringComparison.OrdinalIgnoreCase);
            bool hasStructure = structure != null && !structure.IsEmpty;
            if (shadingSelection != null && ((!isVariant && !shadingSelection.allowSourceOverwrite) || hasStructure ||
                (intent & ~(FbxExportIntent.Normals | FbxExportIntent.Tangents)) != 0))
                throw new InvalidOperationException("Selected shading requires a separate FBX or explicit source overwrite, with normals/tangents intent and no structure edits.");
            // A changed material reaches Unity through the written file's importer remap; a file
            // written outside the project has no importer, so its slots would carry a name only.
            bool inProject = targetFbxPath.StartsWith("Assets/", StringComparison.Ordinal) || targetFbxPath.StartsWith("Packages/", StringComparison.Ordinal);
            if (!inProject && hasStructure && FbxStructureWrite.NeedsImporterRemap(structure, sourceFbxPath))
                throw new FbxStructureRefusalException(
                    $"'{Path.GetFileName(targetFbxPath)}' is written outside the project: the changed materials could only go in as names, without the importer remap that maps them to their assets");

            // The import that follows a source save recreates the file's meshes: the renderers
            // showing them are noted now, by name, so a node whose name differs from its mesh's
            // (Wheel_LOD0 showing Wheel) takes the recreated mesh after the import.
            var bindings = !isVariant && sceneRoot != null && !validationOnly ? FbxExport.CaptureSceneMeshReferences(sourceFbxPath, entries) : null;
            var importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            bool swapUv = importer != null && importer.swapUVChannels;
            bool generatedUv1 = importer != null && importer.generateSecondaryUV;
            // Meshes the structure step writes whole are not channel donors.
            var skip = hasStructure ? structure.WholeMeshNames() : null;
            bool fileNormals = importer != null && importer.importNormals == ModelImporterNormals.Import;
            var capture = new Capture
            {
                intent = intent, generatedUv1 = generatedUv1,
                fileNormals = shadingSelection != null || fileNormals,
                fileTangents = shadingSelection != null || (fileNormals && importer.importTangents == ModelImporterTangents.Import),
                shadingSelection = shadingSelection,
                uv1Baseline = intent.IncludesUv(1) ? ImportUv1Baseline(sourceFbxPath) : null,
            };
            var donors = CaptureDonors(entries, capture, skip);
            // Writing UV1 switches 'Generate Lightmap UVs' off for the whole model, so every
            // mesh's generated UV1 goes into the file with it, edited or not.
            bool bakeUv1 = generatedUv1 && intent.IncludesUv(1)
                && (donors.Any(d => d.uvs[1] != null) || (hasStructure && structure.wholeMeshUv1Edited));
            var unloaded = new HashSet<string>(StringComparer.Ordinal);
            if (bakeUv1)
            {
                donors = CaptureDonors(entries, capture.WithBake(true), skip);
                var captured = CaptureUnloadedUv1(sourceFbxPath, donors, skip);
                unloaded.UnionWith(captured.Select(d => d.name));
                donors.AddRange(captured);
            }
            if (donors.Count == 0 && !hasStructure)
            {
                UvtLog.Warn($"[FBX Export] No meshes carry data for {intent}.");
                return false;
            }

            using var document = FbxSourceDocument.Load(Path.GetFullPath(sourceFbxPath));
            var tagged = ImportTagged(sourceFbxPath, swapUv, out var importedNames);
            LeaveOutUnplaceableUvs(document, donors, tagged, swapUv, Path.GetFileName(sourceFbxPath));
            // An unloaded mesh keeps its polygons: one its imported lightmap UVs do not fit
            // has them unwrapped again instead (WriteRegeneratedUv1).
            int splitPolygons = PrepareSeamSplits(document, donors.Where(d => !unloaded.Contains(d.name)).ToList(), tagged);

            int meshesWritten = 0, cornersWritten = 0;
            var written = new HashSet<int>();
            var channelsWritten = new HashSet<string>();
            var regenerate = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var donor in donors)
            {
                if (!tagged.TryGetValue(donor.name, out var tag))
                {
                    if (unloaded.Contains(donor.name))
                    {
                        regenerate[donor.name] = "its corner tags did not survive the import's mesh compression";
                        continue;
                    }
                    if (shadingSelection != null)
                        throw new InvalidOperationException($"'{donor.name}': no exact FBX corner mapping was found; foliage export was refused.");
                    if (importedNames.Contains(donor.name))
                        throw new InvalidOperationException(
                            $"'{donor.name}': the corner tags could not be decoded safely after import. " +
                            $"Mesh Compression may need to be reduced for '{sourceFbxPath}'; nothing was written.");
                    UvtLog.Warn($"[FBX Export] '{donor.name}' is not a mesh of '{sourceFbxPath}'; skipped.");
                    continue;
                }
                if (tag.ambiguous)
                    throw new FbxStructureRefusalException($"'{donor.name}': several meshes of '{Path.GetFileName(sourceFbxPath)}' import under that name; which one it is cannot be told");
                var mesh = document.Meshes[tag.ordinal];
                RequireSelectedInstances(mesh, donor.name, shadingSelection);
                if (unloaded.Contains(donor.name) && !PairUnloadedUv1)
                {
                    regenerate[donor.name] = "pairing is off";
                    continue;
                }
                int corners;
                try { corners = WriteMesh(mesh, donor, tag, swapUv, document.HasSmoothing(mesh.GetName()) == false, channelsWritten); }
                catch (CornerPairingException ex) when (unloaded.Contains(donor.name))
                {
                    regenerate[donor.name] = ex.reason;
                    continue;
                }
                if (corners == 0) continue;
                if (!written.Add(tag.ordinal))
                    throw new InvalidOperationException($"FBX mesh '{mesh.GetName()}' is instanced by several Unity meshes; it cannot take edits from more than one.");
                meshesWritten++;
                cornersWritten += corners;
            }
            if (regenerate.Count > 0)
            {
                cornersWritten += WriteRegeneratedUv1(document, sourceFbxPath, swapUv, regenerate, channelsWritten, out int regeneratedSplits);
                meshesWritten += regenerate.Count;
                splitPolygons += regeneratedSplits;
            }

            // After the channels: new nodes take the UV sets their source has by now, the ones
            // MeshLab added within the intent, and UV1 when it is being baked for the whole model.
            var structureLog = new List<string>();
            var options = new FbxStructureWrite.Options
            {
                swapUv = swapUv,
                preserveHierarchy = importer != null && importer.preserveHierarchy,
                writeTangents = importer != null && importer.importTangents == ModelImporterTangents.Import,
                requiredUvSets = bakeUv1 ? 2 : 0,
                editableUvSets = EditableUvSets(intent, swapUv, generatedUv1 && !bakeUv1),
                uv1Regenerated = generatedUv1 && !bakeUv1,
                writeColors = (intent & FbxExportIntent.VertexColors) != 0,
                existingRemaps = MaterialRemaps(importer),
                fbxPath = targetFbxPath,
                sourceFbxPath = sourceFbxPath,
            };
            // The FBX meshes the channels write UV1 into, captured before the structure step
            // changes the mesh list; it writes none of them whole.
            var uvMeshes = donors.Where(d => d.uvs[1] != null && tagged.ContainsKey(d.name))
                .Select(d => document.Meshes[tagged[d.name].ordinal]).ToHashSet();
            int structural = hasStructure ? FbxStructureWrite.Apply(document, tagged, structure, options, structureLog) : 0;
            if (structural > 0) document.RefreshMeshes();
            var wholeMeshes = options.wholeMeshes;
            if (wholeMeshes.Count > 0)
            {
                // Meshes written whole carry Unity's normals but no smoothing; give them smoothing
                // that follows those normals. The scene is replaced, so carry meshes by ordinal.
                var uvOrdinals = uvMeshes.Select(m => document.Meshes.IndexOf(m)).ToList();
                var wholeOrdinals = wholeMeshes.Select(m => document.Meshes.IndexOf(m)).ToList();
                if (uvOrdinals.Contains(-1) || wholeOrdinals.Contains(-1))
                    throw new InvalidOperationException("FBX meshes written whole are not in the document; nothing was written.");
                document.SmoothFromNormals(wholeMeshes);
                uvMeshes = uvOrdinals.Select(i => document.Meshes[i]).ToHashSet();
                wholeMeshes = wholeOrdinals.Select(i => document.Meshes[i]).ToList();
                structureLog.Add($"{wholeMeshes.Count} mesh(es) written whole take edge smoothing that follows their normals");
            }
            bool uv1Written = channelsWritten.Contains("UV1") || (options.uv1Written && structural > 0);
            if (uv1Written) {
                if (hasStructure && options.uv1Written) uvMeshes.UnionWith(wholeMeshes);
                int points = document.PreserveSmallUvSeams(uvMeshes, FbxUvSet(1, swapUv));
                if (points > 0) UvtLog.Info($"[FBX Export] Preserved nearby lightmap seams with {points} duplicate control points; positions and UVs unchanged.");
            }

            if (meshesWritten == 0 && structural == 0 && shadingSelection == null)
            {
                UvtLog.Info($"[FBX Export] '{Path.GetFileName(sourceFbxPath)}': nothing changed; file left as is.");
                return false;
            }
            if (bakeUv1 && uv1Written) RequireEveryUv1(sourceFbxPath, donors, hasStructure ? structure.WholeMeshNames() : null, importedNames);

            // The sidecar's UV2 replay is held off for the import that follows only when that
            // import brings a UV1 written (or removed) here; otherwise the sidecar still applies.
            bool uv1Changed = uv1Written || channelsWritten.Contains("UV1 (removed)");
            // Preflight exercises the same donor, instance and corner mapping checks in a disposable document.
            if (validationOnly) return true;
            if (uv1Changed) Uv2AssetPostprocessor.fbxOverwritePaths.Add(targetFbxPath);

            // A write that did not happen takes its marker back; a written file whose steps after the
            // replacement failed keeps it, so the import that follows skips the stale sidecar.
            try { document.SaveAtomic(Path.GetFullPath(targetFbxPath), shadingSelection?.mediaOwnership); }
            catch (Exception ex) when (!(ex is FbxExport.PostCommitException)) { Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath); throw; }
            string format = $"FBX {document.Major}.{document.Minor}, {FbxWriteSettings.FromProject().FormatName}";
            if (meshesWritten > 0)
                UvtLog.Info($"[FBX Export] {meshesWritten} mesh(es), {cornersWritten} corner value(s) of {string.Join(", ", channelsWritten.OrderBy(c => c))} -> {targetFbxPath} " +
                    $"({format}; {splitPolygons} polygon(s) split at new internal seams; other channels retained)");
            foreach (string line in structureLog) UvtLog.Info($"[FBX Export] {line} -> {targetFbxPath} ({format})");

            // A save-as outside the project is a plain file; nothing to import.
            if (!inProject)
            {
                Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath);
                return true;
            }
            // The file is written from here on. Import settings it needs (UV1 kept, material
            // remaps) that cannot be set (read-only metadata) fail the save: the file is still
            // imported, but the caller keeps the working copies unsaved, and a save once the
            // cause is fixed sets them. An import that fails (a third-party postprocessor) is
            // reported, and the caller still finishes the save (sidecar, scene). Partial
            // foliage variants instead propagate import failures and remove the failed output. The
            // postprocessor consumes the overwrite marker; an import that throws before it runs
            // must not leave it for a later, unrelated import.
            bool configured = true;
            try
            {
                try
                {
                    if (!isVariant)
                        configured = SetImportSettings(targetFbxPath, () =>
                        {
                            if (uv1Written) KeepWrittenUv1(sourceFbxPath);
                            MapMaterials(sourceFbxPath, options.materialRemaps);
                        });
                    ImportWrittenAsset(targetFbxPath, shadingSelection);
                }
                finally { Uv2AssetPostprocessor.fbxOverwritePaths.Remove(targetFbxPath); }
                if (isVariant)
                {
                    // A new file has an importer only once imported: its settings follow the first import.
                    configured = SetImportSettings(targetFbxPath, () =>
                    {
                        ConfigureLikeSource(sourceFbxPath, targetFbxPath, uv1Changed, options.materialRemaps);
                        if (shadingSelection != null)
                        {
                            var variant = (ModelImporter)AssetImporter.GetAtPath(targetFbxPath);
                            variant.importNormals = ModelImporterNormals.Import;
                            // A partial shading export must keep the source tangent calculation
                            // mode. Importing stored frames instead would change excluded shading.
                            EditorUtility.SetDirty(variant);
                            AssetDatabase.WriteImportSettingsIfDirty(targetFbxPath);
                        }
                    });
                    if (configured) ReimportVariant(targetFbxPath, uv1Changed, shadingSelection);
                }
                else if (sceneRoot != null)
                {
                    bindings?.Relink(sourceFbxPath);
                    FbxExport.RelinkSceneMeshReferences(sourceFbxPath, null, sceneRoot);
                }
            }
            catch (Exception ex)
            {
                if (shadingSelection != null) throw;
                UvtLog.Error($"[FBX Export] '{targetFbxPath}' was written, but setting up or running its import failed: {ex.Message}. Reimport it once the cause is fixed.");
                UvtLog.Verbose(ex.ToString());
            }
            return configured;
        }

        // A lit mesh of the model the save did not load (another object of the same file)
        // keeps the lightmap UVs it imports with now: they go into the file as they are.
        // A name several meshes share cannot be told apart and is left to RequireEveryUv1.
        static List<Donor> CaptureUnloadedUv1(string sourceFbxPath, List<Donor> donors, ICollection<string> skip)
        {
            var loaded = new HashSet<string>(donors.Select(d => d.name), StringComparer.Ordinal);
            var unloaded = AssetDatabase.LoadAllAssetsAtPath(sourceFbxPath).OfType<Mesh>()
                .GroupBy(m => m.name, StringComparer.Ordinal)
                .Where(g => g.Count() == 1 && !loaded.Contains(g.Key) && !MeshNaming.IsCollision(g.Key) && (skip == null || !skip.Contains(g.Key)))
                .Select(g => g.Single());
            var uv1Only = new Capture { intent = FbxExportIntent.UV1, generatedUv1 = true, bakeUv1 = true };
            var captured = new List<Donor>();
            foreach (var mesh in unloaded)
            {
                var donor = CaptureDonor(new MeshEntry { fbxMesh = mesh }, uv1Only);
                if (donor?.uvs[1] != null) captured.Add(donor);
            }
            if (captured.Count > 0)
                UvtLog.Info($"[FBX Export] 'Generate Lightmap UVs' goes off with this save; {string.Join(", ", captured.Select(d => $"'{d.name}'"))} " +
                    "(not loaded here) keep the lightmap UVs they import with now, written into the file.");
            return captured;
        }

        // With generation switched off, a mesh whose UV1 is not in the file has none: refuse
        // rather than drop the lightmap UVs of meshes the save could not carry.
        static void RequireEveryUv1(string sourceFbxPath, List<Donor> donors, ICollection<string> wholeMeshes, HashSet<string> importedNames)
        {
            var withUv1 = new HashSet<string>(donors.Where(d => d.uvs[1] != null).Select(d => d.name), StringComparer.Ordinal);
            if (wholeMeshes != null) withUv1.UnionWith(wholeMeshes);
            var lost = importedNames.Where(n => !withUv1.Contains(n) && !MeshNaming.IsCollision(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (lost.Count == 0) return;
            throw new InvalidOperationException(
                $"'Generate Lightmap UVs' is on for '{sourceFbxPath}'. Writing UV1 switches it off for the whole model, and " +
                $"{string.Join(", ", lost.Select(n => $"'{n}'"))} would lose the lightmap UVs Unity generates for them " +
                "(their imported lightmap UVs could not be carried into the file: several meshes share the name, or the import has none). " +
                "Load the whole model, or switch the setting off first; nothing was written.");
        }

        // ── Lightmap UVs unwrapped again for unloaded meshes ──

        /// <summary>Tests turn this off to send every unloaded mesh to <see cref="WriteRegeneratedUv1"/>.</summary>
        internal static bool PairUnloadedUv1 { get; set; } = true;

        /// <summary>A donor's vertices cannot be paired with the FBX corners exactly; <see cref="reason"/> says why, briefly.</summary>
        sealed class CornerPairingException : InvalidOperationException
        {
            public readonly string reason;
            public CornerPairingException(string reason, string message) : base(message) { this.reason = reason; }
        }

        /// <summary>
        /// Writes Unity's lightmap unwrap, with the model's own unwrap settings, into the UV1
        /// set of each mesh in <paramref name="reasons"/>: meshes the save did not load whose
        /// imported lightmap UVs could not be paired with the file's corners. Their corners
        /// come from an uncompressed tagged import, welded by position, UV0 and normal as
        /// Unity's import welds them, so the unwrap sees the import's connectivity; a polygon
        /// a chart seam runs through is split along the import's triangles. Returns the
        /// corners written; <paramref name="splitPolygons"/> counts the polygons split.
        /// </summary>
        static int WriteRegeneratedUv1(FbxSourceDocument document, string sourceFbxPath, bool swapUv,
            Dictionary<string, string> reasons, HashSet<string> channelsWritten, out int splitPolygons)
        {
            splitPolygons = 0;
            var tagged = ImportTagged(sourceFbxPath, swapUv, out _, uncompressed: true);
            var settings = LightmapUnwrapSettings(AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter);
            int written = 0;
            foreach (var pair in reasons)
            {
                if (!tagged.TryGetValue(pair.Key, out var tag) || tag.ambiguous)
                    throw new InvalidOperationException(
                        $"'{pair.Key}': its lightmap UVs could not be carried into the file ({pair.Value}), and its corners cannot be told apart to unwrap it again; nothing was written.");
                var topology = new FbxLayerChannels.Topology(document.Meshes[tag.ordinal]);
                var triangles = tag.TriangleCorners();
                var triangleUvs = UnwrapTriangles(pair.Key, tag, topology.CornerCount, triangles, settings);

                var conflicts = new bool[topology.CornerCount];
                var first = new Vector2?[topology.CornerCount];
                for (int i = 0; i < triangles.Length; i++)
                {
                    int c = triangles[i];
                    if (first[c] == null) first[c] = triangleUvs[i];
                    else if (first[c].Value != triangleUvs[i]) conflicts[c] = true;
                }
                var plan = FbxPolygonSplit.Plan(topology.polygonSizes, conflicts, triangles);
                if (plan.splitCount > 0)
                {
                    document.SplitPolygons(new Dictionary<int, FbxPolygonSplit> { [tag.ordinal] = plan });
                    splitPolygons += plan.splitCount;
                }
                var mesh = document.Meshes[tag.ordinal];
                topology = new FbxLayerChannels.Topology(mesh);
                var values = SplitCornerUvs(plan, triangles, triangleUvs, first, topology);
                written += FbxLayerChannels.WriteUv(mesh, FbxUvSet(1, swapUv), topology, values, Enumerable.Repeat(true, topology.CornerCount).ToArray());
                channelsWritten.Add("UV1");
                UvtLog.Warn($"[FBX Export] '{pair.Key}' (not loaded here): its imported lightmap UVs could not be carried into the file ({pair.Value}); " +
                    "Unity's lightmap unwrap of the file's own corners, with this model's unwrap settings, was written instead. Rebake its lightmap if it was baked.");
            }
            return written;
        }

        // The unwrap settings Unity's importer uses for this model's generated lightmap UVs.
        static UnwrapParam LightmapUnwrapSettings(ModelImporter importer)
        {
            UnwrapParam.SetDefaults(out var settings);
            if (importer == null) return settings;
            settings.angleError = Mathf.Clamp01(importer.secondaryUVAngleDistortion / 100f);
            settings.areaError = Mathf.Clamp01(importer.secondaryUVAreaDistortion / 100f);
            settings.hardAngle = importer.secondaryUVHardAngle;
            settings.packMargin = importer.secondaryUVPackMargin / 1024f;
            return settings;
        }

        // Unity's per-triangle lightmap unwrap of the import's triangles (FBX corners), welded
        // by position, UV0 and normal so charts follow the import's connectivity.
        static Vector2[] UnwrapTriangles(string name, Tagged tag, int corners, int[] triangles, UnwrapParam settings)
        {
            var weld = new Dictionary<(Vector3, Vector2, Vector3), int>();
            var vertexOf = new int[corners];
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            for (int c = 0; c < corners; c++)
            {
                vertexOf[c] = -1;
                if (c * 3 >= tag.cornerPositions.Length || float.IsNaN(tag.cornerPositions[c * 3])) continue;
                var key = (new Vector3(tag.cornerPositions[c * 3], tag.cornerPositions[c * 3 + 1], tag.cornerPositions[c * 3 + 2]),
                    tag.uvs[0] != null ? tag.uvs[0][c] : Vector2.zero, tag.normals != null ? tag.normals[c] : Vector3.zero);
                if (!weld.TryGetValue(key, out int v))
                {
                    weld[key] = v = positions.Count;
                    positions.Add(key.Item1); uvs.Add(key.Item2); normals.Add(key.Item3);
                }
                vertexOf[c] = v;
            }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            try
            {
                mesh.SetVertices(positions);
                mesh.SetUVs(0, uvs);
                if (tag.normals != null) mesh.SetNormals(normals);
                mesh.SetTriangles(Array.ConvertAll(triangles, c => vertexOf[c]), 0);
                var uv = Unwrapping.GeneratePerTriangleUV(mesh, settings);
                if (uv == null || uv.Length != triangles.Length)
                    throw new InvalidOperationException($"'{name}': Unity's lightmap unwrap failed; nothing was written.");
                return uv;
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // UV1 per corner of the (possibly split) mesh: a split polygon's triangles take their
        // own unwrap values, an unsplit polygon's corners the value all their triangles agree
        // on, and a corner no triangle covers (a degenerate polygon) another corner's value on
        // its control point, else zero.
        static double[] SplitCornerUvs(FbxPolygonSplit plan, int[] triangles, Vector2[] triangleUvs, Vector2?[] first, in FbxLayerChannels.Topology topology)
        {
            var values = new double[topology.CornerCount * 2];
            var covered = new bool[topology.CornerCount];
            var splitTriangles = SplitTriangleCorners(plan);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                var old = new[] { triangles[t], triangles[t + 1], triangles[t + 2] };
                var sorted = old.OrderBy(c => c).ToArray();
                if (!splitTriangles.TryGetValue((sorted[0], sorted[1], sorted[2]), out var triangle)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int c = triangle.First(n => plan.corners[n] == old[k]);
                    values[c * 2] = triangleUvs[t + k].x; values[c * 2 + 1] = triangleUvs[t + k].y;
                    covered[c] = true;
                }
            }
            for (int c = 0; c < topology.CornerCount; c++)
            {
                if (covered[c] || first[plan.corners[c]] == null) continue;
                var uv = first[plan.corners[c]].Value;
                values[c * 2] = uv.x; values[c * 2 + 1] = uv.y;
                covered[c] = true;
            }
            var byPoint = new Dictionary<int, int>();
            for (int c = 0; c < topology.CornerCount; c++) if (covered[c]) byPoint[topology.cornerControlPoint[c]] = c;
            for (int c = 0; c < topology.CornerCount; c++)
            {
                if (covered[c] || !byPoint.TryGetValue(topology.cornerControlPoint[c], out int from)) continue;
                values[c * 2] = values[from * 2]; values[c * 2 + 1] = values[from * 2 + 1];
            }
            return values;
        }

        // ── Donors ──

        // Unity UV channel ↔ FBX UV set: 'Swap UVs' on the importer exchanges the first two.
        internal static int FbxUvSet(int unityChannel, bool swapUv) => swapUv && unityChannel < 2 ? 1 - unityChannel : unityChannel;

        /// <summary>
        /// Asked whether to save without the UV channels the file cannot take, with the list of
        /// them; false (or null) refuses the save. Whether their values would come back after
        /// the import cannot be told (a sidecar may add the channel, an AO bake may have
        /// changed it), so leaving them out is the user's call. Batch mode refuses.
        /// </summary>
        internal static Func<string, bool> ConfirmLeavingOutUvs { get; set; } = message => !Application.isBatchMode
            && EditorUtility.DisplayDialog("UV channels the FBX cannot take", message, "Save without them", "Cancel");

        /// <summary>
        /// The question for <see cref="ConfirmLeavingOutUvs"/>. A long dialog text is cut off, so
        /// it shows a few channels when the Console has them all, and every one when the Console
        /// line is muted: the user decides with the whole list either way.
        /// </summary>
        internal static string LeaveOutMessage(string fileName, IReadOnlyList<string> channels, bool consoleListsAll)
        {
            int shown = consoleListsAll ? Math.Min(3, channels.Count) : channels.Count;
            string list = string.Join("\n", channels.Take(shown))
                + (channels.Count > shown ? $"\n…and {channels.Count - shown} more (see the Console)" : "");
            return $"'{fileName}': {channels.Count} UV channel(s) cannot be added without inventing the UV sets before them:\n\n{list}\n\n" +
                "Save the rest without them? Cancel writes nothing.";
        }

        // A channel the file cannot take (UV3 on a mesh with UV0 and UV1 only) refuses the
        // save unless the user agrees to save the rest without it.
        static void LeaveOutUnplaceableUvs(FbxSourceDocument document, List<Donor> donors, Dictionary<string, Tagged> tagged, bool swapUv, string fileName)
        {
            var left = new List<(Donor donor, int channel, int sets)>();
            foreach (var donor in donors)
            {
                if (!tagged.TryGetValue(donor.name, out var tag) || tag.ambiguous) continue;
                int sets = FbxLayerChannels.UvElements(document.Meshes[tag.ordinal]).Count;
                foreach (int ch in UnplaceableUvChannels(donor.uvs.Select(uv => uv != null).ToArray(), sets, swapUv))
                    left.Add((donor, ch, sets));
            }
            if (left.Count == 0) return;
            bool logged = UvtLog.Current >= UvtLog.Level.Info && UvtLog.IsCategoryEnabled(UvtLog.Category.General);
            UvtLog.Info($"[FBX Export] '{fileName}': UV channels the file cannot take: " +
                string.Join(", ", left.Select(l => $"'{l.donor.name}' UV{l.channel} ({l.sets} UV set(s) in the file" +
                    (l.donor.importedUvs[l.channel] ? "" : $"; the import has no UV{l.channel}") + ")")) + ".");
            if (ConfirmLeavingOutUvs == null || !ConfirmLeavingOutUvs(
                    LeaveOutMessage(fileName, left.Select(l => $"'{l.donor.name}': UV{l.channel}").ToList(), logged)))
                throw new InvalidOperationException($"{string.Join("; ", left.Select(l => $"'{l.donor.name}': UV{l.channel}"))} cannot be added without inventing " +
                    "the UV sets before them; write the channel to the next free UV set, or save without it. Nothing was written.");
            foreach (var (donor, channel, sets) in left)
            {
                donor.uvs[channel] = null;
                UvtLog.Warn($"[FBX Export] '{donor.name}': UV{channel} is not saved: its FBX mesh has {sets} UV set(s), and UV{channel} cannot be added without inventing the sets before it.");
            }
        }

        /// <summary>
        /// The Unity UV channels of <paramref name="present"/> a mesh with <paramref name="existingSets"/>
        /// UV sets cannot take. A new set goes right after the last one, in FBX set order, so a
        /// channel past a gap is left out, and every channel after it.
        /// </summary>
        internal static List<int> UnplaceableUvChannels(bool[] present, int existingSets, bool swapUv)
        {
            var left = new List<int>();
            int sets = existingSets;
            foreach (int ch in Enumerable.Range(0, present.Length).Where(c => present[c]).OrderBy(c => FbxUvSet(c, swapUv)))
            {
                int set = FbxUvSet(ch, swapUv);
                if (set < sets) continue;
                if (set == sets && set < FbxLayerChannels.MaxUnityUvChannels) { sets++; continue; }
                left.Add(ch);
            }
            return left;
        }

        // What a capture takes from each working mesh.
        sealed class Capture
        {
            public FbxExportIntent intent;
            public bool generatedUv1, bakeUv1;
            /// <summary>The importer reads normals / tangents from the file; otherwise Unity computes them and a changed set is no edit of it.</summary>
            public bool fileNormals, fileTangents;

            public FbxExport.ShadingSelection shadingSelection;
            public Dictionary<string, Uv1Baseline> uv1Baseline;
            public Capture WithBake(bool bake) => new Capture
                { intent = intent, generatedUv1 = generatedUv1, bakeUv1 = bake, fileNormals = fileNormals, fileTangents = fileTangents,
                    shadingSelection = shadingSelection, uv1Baseline = uv1Baseline };
        }

        sealed class Uv1Baseline
        {
            readonly Vector3[] positions;
            readonly Vector2[] uvs;
            readonly Vector2[][] channels = new Vector2[8][];
            readonly int[] faces, faceSizes;

            public Uv1Baseline(Mesh mesh)
            {
                positions = mesh.vertices;
                uvs = mesh.uv2;
                var list = new List<Vector2>();
                for (int ch = 0; ch < channels.Length; ch++)
                {
                    mesh.GetUVs(ch, list);
                    channels[ch] = list.Count == mesh.vertexCount ? list.ToArray() : null;
                }
                ReadFaces(mesh, out faces, out faceSizes);
            }

            /// <summary><see cref="KeepsImportedUvs"/> against this import, for a mesh whose vertex count differs from it.</summary>
            public bool Keeps(Mesh mesh, List<Vector2> values, int channel)
            {
                var uv0 = new List<Vector2>();
                mesh.GetUVs(0, uv0);
                return KeepsImportedUvs(mesh.vertices, uv0, values, positions, channels[0], channels[channel], faces);
            }

            public bool Matches(Mesh mesh, List<Vector2> values)
            {
                if (!uvs.SequenceEqual(values) || !positions.SequenceEqual(mesh.vertices)) return false;
                ReadFaces(mesh, out var otherFaces, out var otherSizes);
                return faces.SequenceEqual(otherFaces) && faceSizes.SequenceEqual(otherSizes);
            }
        }

        static void RequireSelectedInstances(Autodesk.Fbx.FbxMesh mesh, string name, FbxExport.ShadingSelection selection)
        {
            if (selection == null || mesh.GetNodeCount() <= 1) return;
            var expected = Enumerable.Range(0, mesh.GetNodeCount()).Select(i => mesh.GetNode(i).GetName()).ToArray();
            if (expected.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
                !selection.sourceNodes.TryGetValue(name, out var captured) || !expected.All(captured.Contains))
                throw new InvalidOperationException($"'{name}' is instanced in the FBX. Capture every source-model node using this mesh with compatible foliage edits; excluded or unidentifiable instances cannot be changed. Nothing was written.");
        }

        static List<Donor> CaptureDonors(IEnumerable<MeshEntry> entries, Capture capture, ICollection<string> skip)
        {
            var donors = new List<Donor>();
            var byName = new Dictionary<string, (Donor donor, MeshEntry entry)>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e != null && e.include))
            {
                var donor = CaptureDonor(entry, capture);
                if (donor == null || (skip != null && skip.Contains(donor.name))) continue;
                // Instances of one FBX mesh share its data: the same edit twice is one edit,
                // different edits cannot both be written. Imports that differ are not instances
                // but different meshes under one name, which the save cannot tell apart.
                if (byName.TryGetValue(donor.name, out var first))
                {
                    if (!SameEdit(first.donor, donor))
                        throw new InvalidOperationException(MeshInstances.SameContent(first.entry.fbxMesh, entry.fbxMesh)
                            ? $"'{donor.name}' is instanced and its instances were edited differently; they share one FBX mesh, so only one edit can be written. Nothing was written."
                            : $"'{donor.name}' names several different meshes of the FBX, and the save tells the file's meshes apart by name; give them unique names in the DCC. Nothing was written.");
                    continue;
                }
                byName[donor.name] = (donor, entry);
                donors.Add(donor);
            }
            return donors;
        }

        // The entry's working mesh with the intent's channels it edited, or null when it edited
        // none. With bakeUv1, its UV1 counts as edited whatever it holds.
        static Donor CaptureDonor(MeshEntry entry, Capture capture)
        {
            var source = entry.originalMesh ?? entry.fbxMesh;
            var working = entry.repackedMesh ?? entry.transferredMesh ?? source;
            var identity = entry.fbxMesh ?? source;
            if (working == null || identity == null) return null;
            // A Read/Write-disabled import is untouched and edited nothing; when UV1 is baked
            // its generated UV1 still goes into the file, read through a copy.
            if (!working.isReadable && !capture.bakeUv1) return null;
            var mesh = MeshAccess.Readable(working, out bool isCopy);
            try { return CaptureDonor(entry, identity.name, working, mesh, capture); }
            finally { if (isCopy) UnityEngine.Object.DestroyImmediate(mesh); }
        }

        // working: the entry's mesh; mesh: the same, readable.
        static Donor CaptureDonor(MeshEntry entry, string name, Mesh working, Mesh mesh, Capture capture)
        {
            var intent = capture.intent;
            bool generatedUv1 = capture.generatedUv1;
            var donor = new Donor { name = name };
            if (capture.shadingSelection != null)
            {
                if (!capture.shadingSelection.submeshes.TryGetValue(name, out donor.shadingSubmeshes) || donor.shadingSubmeshes.Length != mesh.subMeshCount)
                    throw new InvalidOperationException($"'{name}': the foliage submesh selection no longer matches the mesh.");
                donor.submeshVertices = new HashSet<int>[mesh.subMeshCount];
                for (int s = 0; s < mesh.subMeshCount; s++) donor.submeshVertices[s] = new HashSet<int>(mesh.GetIndices(s));
            }
            // Most channels matching the current import were not edited here and can be
            // left out. Lightmap UVs use an import without sidecars: Apply/replay can
            // replace them even while Generate Lightmap UVs is enabled. A
            // mesh edited in place (the working copy is the asset mesh) cannot be told
            // apart, so its channels go on to the per-corner comparison.
            bool inPlace = working == entry.fbxMesh;
            var imported = entry.fbxMesh != null && !inPlace && entry.fbxMesh.isReadable
                && entry.fbxMesh.vertexCount == mesh.vertexCount ? entry.fbxMesh : null;
            for (int ch = 0; ch < 8; ch++)
                donor.importedUvs[ch] = entry.fbxMesh != null && entry.fbxMesh.HasVertexAttribute((VertexAttribute)((int)VertexAttribute.TexCoord0 + ch));
            // A weld or a seam split changes the vertex count, and a Read/Write-disabled import
            // cannot be compared in place: its channels are then compared vertex by vertex, by
            // position and UV0, with the sidecar-free import the UV1 comparison uses (a sidecar's
            // replay is an edit to write, not a baseline). Without it they count as edited.
            var weldedBaseline = imported == null && entry.fbxMesh != null && !inPlace && capture.uv1Baseline != null
                && capture.uv1Baseline.TryGetValue(name, out var baseline) ? baseline : null;
            bool any = CaptureUvs(donor, mesh, imported, weldedBaseline, capture);
            if ((intent & FbxExportIntent.VertexColors) != 0) any |= CaptureColors(donor, mesh, imported);
            any |= CaptureRemovals(donor, mesh, inPlace ? null : entry.fbxMesh, intent, generatedUv1);
            any |= CaptureShading(donor, mesh, imported, inPlace ? null : entry.fbxMesh, capture);
            if (!any) return null;
            // Colours not written still tell coincident polygons apart.
            if (donor.colors == null && mesh.HasVertexAttribute(VertexAttribute.Color))
            {
                if (mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8) donor.signatureColors32 = mesh.colors32;
                else donor.signatureColors = mesh.colors;
            }

            var vertices = mesh.vertices;
            donor.positions = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                donor.positions[i * 3] = vertices[i].x; donor.positions[i * 3 + 1] = vertices[i].y; donor.positions[i * 3 + 2] = vertices[i].z;
            }
            ReadFaces(mesh, out donor.faces, out donor.faceSizes);
            return donor;
        }

        static bool CaptureUvs(Donor donor, Mesh mesh, Mesh imported, Uv1Baseline weldedBaseline, Capture capture)
        {
            bool any = false;
            bool generatedUv1 = capture.generatedUv1;
            var list = new List<Vector2>();
            var importedList = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                mesh.GetUVs(ch, list);
                if (list.Count != mesh.vertexCount) continue;
                if (!capture.intent.IncludesUv(ch))
                {
                    if (ch != 1 || !generatedUv1) donor.signatureUvs[ch] = list.ToArray();
                    continue;
                }
                // The current import is not a baseline for UV1: sidecar replay may
                // already have changed it. Compare with a disposable import using
                // the same settings, including Unity's generation, but no sidecar.
                if (ch == 1)
                {
                    if (!capture.bakeUv1 && capture.uv1Baseline != null
                        && capture.uv1Baseline.TryGetValue(donor.name, out var baseline)
                        && baseline != null && baseline.Matches(mesh, list))
                    {
                        if (!generatedUv1) donor.signatureUvs[ch] = list.ToArray();
                        continue;
                    }
                }
                else if (imported != null)
                {
                    imported.GetUVs(ch, importedList);
                    if (importedList.SequenceEqual(list))
                    {
                        donor.signatureUvs[ch] = list.ToArray();
                        continue;
                    }
                }
                else if (weldedBaseline != null && weldedBaseline.Keeps(mesh, list, ch))
                {
                    donor.signatureUvs[ch] = list.ToArray();
                    continue;
                }
                donor.uvs[ch] = list.ToArray();
                any = true;
            }
            return any;
        }

        /// <summary>
        /// True when a UV channel came through a weld or a seam split unchanged: each position and
        /// UV0 the import's faces use carries one value, every vertex holds the value of its
        /// position and UV0, and none of them is gone. A position and UV0 with two values (whose
        /// corners cannot be told apart) or a merge that dropped one counts as an edit.
        /// </summary>
        internal static bool KeepsImportedUvs(Vector3[] positions, IList<Vector2> uv0, IList<Vector2> values,
            Vector3[] importedPositions, IList<Vector2> importedUv0, IList<Vector2> importedValues, IEnumerable<int> importedCorners)
        {
            if (values == null || importedValues == null || values.Count != positions.Length || importedValues.Count != importedPositions.Length) return false;
            bool withUv0 = uv0 != null && importedUv0 != null && uv0.Count == positions.Length && importedUv0.Count == importedPositions.Length;
            var byKey = new Dictionary<(Vector3, Vector2), Vector2>();
            foreach (int v in importedCorners)
            {
                var key = (importedPositions[v], withUv0 ? importedUv0[v] : default);
                if (!byKey.TryGetValue(key, out var value)) byKey.Add(key, importedValues[v]);
                else if (!value.Equals(importedValues[v])) return false;
            }
            var kept = new HashSet<(Vector3, Vector2)>();
            for (int i = 0; i < positions.Length; i++)
            {
                var key = (positions[i], withUv0 ? uv0[i] : default);
                if (!byKey.TryGetValue(key, out var value) || !value.Equals(values[i])) return false;
                kept.Add(key);
            }
            return kept.Count == byKey.Count;
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

        // Normals and tangents the working mesh changed, added or dropped. They count only
        // where the importer reads them from the file; under Calculate Unity recomputes them on
        // every import, so a changed set is reported and left out of the file.
        static bool CaptureShading(Donor donor, Mesh mesh, Mesh imported, Mesh importedAny, Capture capture)
        {
            // The asset mesh itself (edited in place, or untouched): nothing to tell apart.
            if (importedAny == null) return false;
            bool any = false;
            bool hasNormals = mesh.HasVertexAttribute(VertexAttribute.Normal);
            bool hasTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
            if ((capture.intent & FbxExportIntent.Normals) != 0)
            {
                if (hasNormals)
                {
                    var normals = mesh.normals;
                    // An unrenumbered copy compares by index; a renumbered one goes on to the per-corner comparison.
                    bool unchanged = capture.shadingSelection == null && imported != null && imported.normals.SequenceEqual(normals);
                    if (unchanged) { /* nothing to write */ }
                    else if (capture.fileNormals) { donor.normals = normals; any = true; }
                    else if (imported != null)
                        UvtLog.Warn($"[FBX Export] '{donor.name}': its normals changed, but the importer calculates normals, so they are not saved into the file.");
                }
                else if (importedAny.HasVertexAttribute(VertexAttribute.Normal))
                {
                    if (capture.fileNormals) { donor.removedNormals = true; any = true; }
                    else UvtLog.Warn($"[FBX Export] '{donor.name}': its normals were removed, but the importer calculates normals, so the file's normals are left as they are.");
                }
            }
            if ((capture.intent & FbxExportIntent.Tangents) != 0 && !capture.fileTangents && imported != null
                && (hasTangents != imported.HasVertexAttribute(VertexAttribute.Tangent) || (hasTangents && !imported.tangents.SequenceEqual(mesh.tangents))))
                UvtLog.Warn($"[FBX Export] '{donor.name}': its tangents changed, but the importer calculates tangents, so they are not saved into the file.");
            // A written normal moves the binormal (cross(normal, tangent)·w), so the tangent
            // frame follows written normals even when only the normals were asked for.
            bool tangentIntent = (capture.intent & FbxExportIntent.Tangents) != 0;
            if (capture.fileTangents && (tangentIntent || donor.normals != null))
            {
                if (hasTangents && hasNormals)
                {
                    var tangents = mesh.tangents;
                    bool unchanged = donor.normals == null && imported != null && imported.tangents.SequenceEqual(tangents);
                    if (!unchanged)
                    {
                        donor.tangents = tangents; donor.frameNormals = mesh.normals; any = true;
                        // Under a normals-only save the tangents only follow the normals, whatever
                        // can be compared: a file without a frame gets none.
                        donor.tangentsFollowNormals = donor.normals != null
                            && (!tangentIntent || imported != null && imported.tangents.SequenceEqual(tangents));
                        donor.keepFileTangents = donor.normals != null && !tangentIntent;
                    }
                }
                else if (!tangentIntent)
                {
                    // A normals-only save adds or drops no tangents; but new normals under the
                    // file's old frame (the mesh dropped its tangents) would leave its binormals stale.
                    if (hasNormals && importedAny.HasVertexAttribute(VertexAttribute.Tangent))
                        throw new FbxStructureRefusalException($"'{donor.name}': its normals changed and its tangents were removed; save tangents too, so the FBX tangent frame goes with the normals");
                }
                else if (hasTangents)
                {
                    // The file's binormal is built from the normal: a mesh whose normals were
                    // dropped has no frame to keep (the old one would be stale once Unity
                    // recalculates the normals) or to write.
                    if (donor.removedNormals || imported == null || !imported.tangents.SequenceEqual(mesh.tangents))
                        throw new FbxStructureRefusalException($"'{donor.name}': its normals were removed while its tangents were kept; the FBX tangent frame needs the normals (remove the tangents too, or keep the normals)");
                }
                else if (importedAny.HasVertexAttribute(VertexAttribute.Tangent))
                {
                    donor.removedTangents = true;
                    any = true;
                }
            }
            // Normals removed under a normals-only save would leave the file's tangent frame
            // built on normals Unity no longer has.
            if (donor.removedNormals && !tangentIntent && capture.fileTangents && importedAny.HasVertexAttribute(VertexAttribute.Tangent))
                throw new FbxStructureRefusalException($"'{donor.name}': its normals were removed while the file keeps a tangent frame built on them; save tangents too");
            return any;
        }

        // A channel the import carries and the working mesh dropped is a removal to write.
        // Lightmap UVs Unity generates are not in the file and cannot be removed from it.
        static bool CaptureRemovals(Donor donor, Mesh mesh, Mesh imported, FbxExportIntent intent, bool generatedUv1)
        {
            if (imported == null || imported == mesh) return false;
            bool any = false;
            for (int ch = 0; ch < 8; ch++)
            {
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + ch);
                if (!intent.IncludesUv(ch) || (ch == 1 && generatedUv1)) continue;
                if (!imported.HasVertexAttribute(attribute) || mesh.HasVertexAttribute(attribute)) continue;
                donor.removedUvs[ch] = true;
                any = true;
            }
            if ((intent & FbxExportIntent.VertexColors) != 0
                && imported.HasVertexAttribute(VertexAttribute.Color) && !mesh.HasVertexAttribute(VertexAttribute.Color))
                donor.removedColors = any = true;
            return any;
        }

        static bool SameEdit(Donor a, Donor b)
        {
            if (!a.removedUvs.SequenceEqual(b.removedUvs) || a.removedColors != b.removedColors
                || a.removedNormals != b.removedNormals || a.removedTangents != b.removedTangents) return false;
            if (!SameArray(a.normals, b.normals) || !SameArray(a.tangents, b.tangents)) return false;
            if (!a.positions.SequenceEqual(b.positions) || !a.faces.SequenceEqual(b.faces)) return false;
            for (int ch = 0; ch < 8; ch++)
                if ((a.uvs[ch] == null) != (b.uvs[ch] == null) || (a.uvs[ch] != null && !a.uvs[ch].SequenceEqual(b.uvs[ch]))) return false;
            return (a.colors == null) == (b.colors == null) && (a.colors == null || a.colors.SequenceEqual(b.colors));
        }

        static bool SameArray<T>(T[] a, T[] b) => (a == null) == (b == null) && (a == null || a.SequenceEqual(b));

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

        // Corner tags split vertices and can change Unity's generated unwrap. Use an
        // untagged copy to identify unchanged UV1, and the other UV channels of a welded
        // mesh; the source asset is never reimported.
        static Dictionary<string, Uv1Baseline> ImportUv1Baseline(string sourceFbxPath)
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            string tempPath = $"{TempFolder}/uv1_baseline_{Guid.NewGuid():N}.fbx";
            Uv2AssetPostprocessor.bypassPaths.Add(tempPath);
            try
            {
                if (!AssetDatabase.CopyAsset(sourceFbxPath, tempPath))
                    throw new InvalidOperationException("Could not copy the source FBX for lightmap UV comparison.");
                FbxExport.ReimportWithoutSidecars(tempPath, () => AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceSynchronousImport));
                var importer = AssetImporter.GetAtPath(tempPath) as ModelImporter;
                if (importer == null) throw new InvalidOperationException("The lightmap UV baseline did not import as a model.");
                importer.isReadable = true;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                FbxExport.ReimportWithoutSidecars(tempPath, importer.SaveAndReimport);

                var baselines = new Dictionary<string, Uv1Baseline>(StringComparer.Ordinal);
                foreach (var mesh in AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<Mesh>())
                    // Duplicate mesh names cannot provide a safe equality shortcut.
                    baselines[mesh.name] = baselines.ContainsKey(mesh.name) ? null : new Uv1Baseline(mesh);
                return baselines;
            }
            finally
            {
                Uv2AssetPostprocessor.bypassPaths.Remove(tempPath);
                AssetDatabase.DeleteAsset(tempPath);
                if (AssetDatabase.IsValidFolder(TempFolder) && AssetDatabase.FindAssets(string.Empty, new[] { TempFolder }).Length == 0)
                    AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        internal static Dictionary<string, Tagged> ImportTagged(string sourceFbxPath, bool swapUv, out HashSet<string> importedNames, bool uncompressed = false)
        {
            importedNames = new HashSet<string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            string tempPath = $"{TempFolder}/corner_tags_{Guid.NewGuid():N}.fbx";
            var tagChannels = new Dictionary<int, int>();
            var cornerCounts = new Dictionary<int, int>();
            // A mesh with all eight UV sets gives its last one to the tag; that set's values come
            // from the file instead, per corner, so the corner matching still sees them.
            var takenSets = new Dictionary<int, double[]>();
            Uv2AssetPostprocessor.bypassPaths.Add(tempPath);
            try
            {
                using (var tagDocument = FbxSourceDocument.Load(Path.GetFullPath(sourceFbxPath)))
                {
                    AddCornerTags(tagDocument, swapUv, tagChannels, takenSets, cornerCounts);
                    tagDocument.SaveForImport(Path.GetFullPath(tempPath));
                }

                AssetDatabase.ImportAsset(tempPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(tempPath) as ModelImporter;
                var source = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
                if (importer == null) throw new InvalidOperationException("The tagged copy did not import as a model.");
                // Same settings as the source so vertices land where the working meshes'
                // are; nothing else is needed from this import.
                if (source != null) new Preset(source).ApplyTo(importer);
                if (uncompressed) importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.isReadable = true;
                importer.generateSecondaryUV = false;
                importer.importAnimation = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                Uv2AssetPostprocessor.bypassPaths.Add(tempPath);
                importer.SaveAndReimport();

                var tagged = new Dictionary<string, Tagged>(StringComparer.Ordinal);
                if (importer.meshCompression == ModelImporterMeshCompression.Off)
                {
                    ReadImportedTags(tempPath, tagChannels, cornerCounts, takenSets, importedNames, tagged);
                    return tagged;
                }
                // Mesh Compression quantizes the tag channel too: a large mesh's corner indices
                // do not survive it. They are read from an uncompressed import of the same copy,
                // whose vertices are the compressed import's in the same order; positions and
                // channels stay the compressed import's, which the working meshes share.
                var compressed = AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<Mesh>()
                    .Select(m => { var copy = Object.Instantiate(m); copy.name = m.name; return copy; }).ToList();
                try
                {
                    importer = (ModelImporter)AssetImporter.GetAtPath(tempPath);
                    importer.meshCompression = ModelImporterMeshCompression.Off;
                    importer.SaveAndReimport();
                    ReadImportedTags(tempPath, tagChannels, cornerCounts, takenSets, importedNames, tagged, compressed);
                }
                finally { foreach (var copy in compressed) Object.DestroyImmediate(copy); }
                return tagged;
            }
            finally
            {
                Uv2AssetPostprocessor.bypassPaths.Remove(tempPath);
                // A copy that failed before its import is a plain file the database does not know.
                if (!AssetDatabase.DeleteAsset(tempPath))
                {
                    File.Delete(Path.GetFullPath(tempPath));
                    File.Delete(Path.GetFullPath(tempPath + ".meta"));
                }
                if (AssetDatabase.IsValidFolder(TempFolder) && AssetDatabase.FindAssets(string.Empty, new[] { TempFolder }).Length == 0)
                    AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        static void AddCornerTags(FbxSourceDocument document, bool swapUv, Dictionary<int, int> channels, Dictionary<int, double[]> takenSets, Dictionary<int, int> cornerCounts)
        {
            for (int i = 0; i < document.Meshes.Count; i++)
            {
                var mesh = document.Meshes[i];
                var sets = FbxLayerChannels.UvElements(mesh);
                if (sets.Count >= FbxLayerChannels.MaxUnityUvChannels)
                    takenSets[i] = FbxLayerChannels.ReadUv(sets[FbxLayerChannels.MaxUnityUvChannels - 1], new FbxLayerChannels.Topology(mesh));
                channels[i] = FbxUvSet(FbxLayerChannels.AddCornerTag(mesh, i), swapUv);
                cornerCounts[i] = new FbxLayerChannels.Topology(mesh).CornerCount;
            }
        }

        // With vertexSources (the compressed import's meshes, in import order) the tags come from
        // the meshes at path and everything else from the source at the same index; a source
        // whose vertices or faces differ is read on its own, as without them.
        static void ReadImportedTags(string path, Dictionary<int, int> tagChannels, Dictionary<int, int> cornerCounts, Dictionary<int, double[]> takenSets,
            HashSet<string> importedNames, Dictionary<string, Tagged> tagged, List<Mesh> vertexSources = null)
        {
            var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToList();
            bool paired = vertexSources != null && vertexSources.Count == meshes.Count;
            for (int i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                importedNames.Add(mesh.name);
                Tagged tag;
                if (vertexSources == null) tag = ReadTags(mesh, tagChannels, cornerCounts);
                else if (paired && SameFaces(mesh, vertexSources[i])) tag = ReadTags(mesh, tagChannels, cornerCounts, vertexSources[i]);
                else
                {
                    UvtLog.Verbose($"[FBX Export] '{mesh.name}': its uncompressed import differs from the compressed one; its corner tags are read from the compressed import.");
                    tag = i < vertexSources.Count && vertexSources[i].name == mesh.name ? ReadTags(vertexSources[i], tagChannels, cornerCounts) : null;
                }
                if (tag == null) continue;
                if (tagged.TryGetValue(mesh.name, out var first)) { first.ambiguous = true; continue; }
                if (takenSets.TryGetValue(tag.ordinal, out var values)) tag.uvs[tagChannels[tag.ordinal]] = PerCornerUvs(values, tag.cornerPositions.Length / 3);
                tagged[mesh.name] = tag;
            }
        }

        static bool SameFaces(Mesh a, Mesh b)
        {
            if (a.name != b.name || a.vertexCount != b.vertexCount || a.subMeshCount != b.subMeshCount) return false;
            for (int s = 0; s < a.subMeshCount; s++)
                if (a.GetTopology(s) != b.GetTopology(s) || !a.GetIndices(s).SequenceEqual(b.GetIndices(s))) return false;
            return true;
        }

        // The tag channel is read from mesh; the vertices, faces and other channels from
        // vertexSource when given (the same vertices, in the same order, see ReadImportedTags).
        internal static Tagged ReadTags(Mesh mesh, Dictionary<int, int> tagChannels, Dictionary<int, int> cornerCounts, Mesh vertexSource = null)
        {
            var list = new List<Vector2>();
            var faceVertices = FaceVertices(mesh);
            for (int ch = 0; ch < 8; ch++)
            {
                mesh.GetUVs(ch, list);
                if (list.Count == 0 || list.Count != mesh.vertexCount) continue;
                int ordinal = TagOrdinal(list, faceVertices);
                if (ordinal < 0 || !tagChannels.TryGetValue(ordinal, out int expected) || expected != ch) continue;
                int count = cornerCounts[ordinal];
                if (list.Any(t => TagCorner(t, ordinal) >= count)) continue;
                return BuildTagged(vertexSource ?? mesh, ch, ordinal, list, count);
            }
            return null;
        }

        // The vertices a triangle or quad of the mesh uses.
        static bool[] FaceVertices(Mesh mesh)
        {
            var used = new bool[mesh.vertexCount];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var topology = mesh.GetTopology(s);
                if (topology != MeshTopology.Triangles && topology != MeshTopology.Quads) continue;
                foreach (int i in mesh.GetIndices(s)) used[i] = true;
            }
            return used;
        }

        // The ordinal every vertex of the channel agrees on, or -1 when it is not a tag channel.
        // With faceVertices, a vertex no face uses does not vote: a control point outside every
        // polygon imports as such a vertex with no corner, so its tag is (0, 0).
        internal static int TagOrdinal(List<Vector2> tags, bool[] faceVertices = null)
        {
            int ordinal = -1;
            for (int v = 0; tags != null && v < tags.Count; v++)
            {
                if (faceVertices != null && !faceVertices[v]) continue;
                if (!DecodeTag(tags[v], out _, out int candidate) || ordinal >= 0 && candidate != ordinal) return -1;
                ordinal = candidate;
            }
            return ordinal;
        }

        internal static bool DecodeTag(Vector2 tag, out int corner, out int ordinal)
        {
            corner = ordinal = -1;
            int radix = FbxLayerChannels.CornerTagRadix;
            if (!IsWhole(tag.x, out int low) || low < 0 || low >= radix
                || !IsWhole(tag.y, out int packed) || packed >= 0 || packed < -(1 << 24)) return false;
            int value = -packed - 1;
            ordinal = value / radix;
            corner = (value % radix) * radix + low;
            return true;
        }

        // The corner a vertex carries, or -1 when its tag is not one of this mesh's (a vertex no
        // face uses).
        static int TagCorner(Vector2 tag, int ordinal)
            => DecodeTag(tag, out int corner, out int tagOrdinal) && tagOrdinal == ordinal ? corner : -1;

        // Each varying component spans at most 4095. Real compressed imports can exceed
        // 0.002 even on small meshes; retain a strict margin to the half-integer boundary.
        // Non-finite, out-of-range and mixed-ordinal IDs still fail before any file write.
        static bool IsWhole(float f, out int whole)
        {
            whole = 0;
            if (float.IsNaN(f) || float.IsInfinity(f) || f < -(1 << 24) || f >= 1 << 24) return false;
            whole = Mathf.RoundToInt(f);
            return Mathf.Abs(f - whole) < .125f;
        }

        // The file's per-corner UV values as Unity imports them (single precision).
        static Vector2[] PerCornerUvs(double[] values, int corners)
        {
            var uvs = new Vector2[corners];
            for (int c = 0; c < corners && c * 2 + 1 < values.Length; c++) uvs[c] = new Vector2((float)values[c * 2], (float)values[c * 2 + 1]);
            return uvs;
        }

        static Tagged BuildTagged(Mesh mesh, int tagChannel, int ordinal, List<Vector2> tags, int corners)
        {
            var tag = new Tagged { ordinal = ordinal, cornerPositions = new float[corners * 3] };
            for (int i = 0; i < tag.cornerPositions.Length; i++) tag.cornerPositions[i] = float.NaN;

            var vertices = mesh.vertices;
            var cornerOf = new int[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                int c = cornerOf[v] = TagCorner(tags[v], ordinal);
                if (c < 0) continue;
                tag.cornerPositions[c * 3] = vertices[v].x; tag.cornerPositions[c * 3 + 1] = vertices[v].y; tag.cornerPositions[c * 3 + 2] = vertices[v].z;
            }
            tag.submeshCorners = new int[mesh.subMeshCount][];
            tag.submeshFaceSizes = new int[mesh.subMeshCount];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var topology = mesh.GetTopology(s);
                bool faces = topology == MeshTopology.Triangles || topology == MeshTopology.Quads;
                tag.submeshCorners[s] = faces ? Array.ConvertAll(mesh.GetIndices(s), i => cornerOf[i]) : new int[0];
                tag.submeshFaceSizes[s] = topology == MeshTopology.Quads ? 4 : 3;
            }
            ReadTaggedUvs(mesh, tagChannel, corners, cornerOf, tag);
            ReadTaggedShading(mesh, corners, cornerOf, tag);
            return tag;
        }

        static void ReadTaggedUvs(Mesh mesh, int tagChannel, int corners, int[] cornerOf, Tagged tag)
        {
            var list = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                if (ch == tagChannel) continue;
                mesh.GetUVs(ch, list);
                if (list.Count != mesh.vertexCount) continue;
                tag.uvs[ch] = new Vector2[corners];
                for (int v = 0; v < mesh.vertexCount; v++) if (cornerOf[v] >= 0) tag.uvs[ch][cornerOf[v]] = list[v];
            }
        }

        static void ReadTaggedShading(Mesh mesh, int corners, int[] cornerOf, Tagged tag)
        {
            var normals = mesh.normals;
            if (normals != null && normals.Length == mesh.vertexCount)
            {
                tag.normals = new Vector3[corners];
                for (int v = 0; v < mesh.vertexCount; v++) if (cornerOf[v] >= 0) tag.normals[cornerOf[v]] = normals[v];
            }
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == mesh.vertexCount)
            {
                tag.tangents = new Vector4[corners];
                for (int v = 0; v < mesh.vertexCount; v++) if (cornerOf[v] >= 0) tag.tangents[cornerOf[v]] = tangents[v];
            }
            var colors = mesh.colors;
            if (colors != null && colors.Length == mesh.vertexCount)
            {
                var colors32 = mesh.colors32;
                tag.colors = new Color[corners];
                tag.colors32 = new Color32[corners];
                for (int v = 0; v < mesh.vertexCount; v++)
                    if (cornerOf[v] >= 0) { tag.colors[cornerOf[v]] = colors[v]; tag.colors32[cornerOf[v]] = colors32[v]; }
            }
        }


        // ── Per mesh ──

        // Returns the number of corner values written into the document.
        static int WriteMesh(Autodesk.Fbx.FbxMesh mesh, Donor donor, Tagged tag, bool swapUv, bool unsmoothed, HashSet<string> channelsWritten)
        {
            int written = donor.HasValues ? WriteValues(mesh, donor, tag, swapUv, unsmoothed, channelsWritten) : 0;
            return written + WriteRemovals(mesh, donor, swapUv, channelsWritten);
        }

        // Removed channels go last and from the highest FBX set down, so each is the last set
        // when it goes; one before a kept set would renumber it and is refused.
        static int WriteRemovals(Autodesk.Fbx.FbxMesh mesh, Donor donor, bool swapUv, HashSet<string> channelsWritten)
        {
            int removed = 0;
            foreach (int ch in Enumerable.Range(0, 8).Where(c => donor.removedUvs[c]).OrderByDescending(c => FbxUvSet(c, swapUv)))
            {
                int set = FbxUvSet(ch, swapUv);
                if (set >= FbxLayerChannels.UvElements(mesh).Count) continue;
                FbxLayerChannels.RemoveUv(mesh, set);
                channelsWritten.Add($"UV{ch} (removed)");
                removed++;
            }
            if (donor.removedColors && FbxLayerChannels.RemoveColor(mesh))
            {
                channelsWritten.Add("vertex colours (removed)");
                removed++;
            }
            if (donor.removedNormals && FbxLayerChannels.RemoveNormals(mesh))
            {
                channelsWritten.Add("normals (removed)");
                removed++;
            }
            if (donor.removedTangents && FbxLayerChannels.RemoveTangentFrame(mesh))
            {
                channelsWritten.Add("tangents (removed)");
                removed++;
            }
            return removed;
        }

        static int WriteValues(Autodesk.Fbx.FbxMesh mesh, Donor donor, Tagged tag, bool swapUv, bool unsmoothed, HashSet<string> channelsWritten)
        {
            var topology = new FbxLayerChannels.Topology(mesh);
            PrepareShadingSelection(donor, tag, topology.CornerCount);
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
                    // Written values are clamped to [0, 1] (checklist §4); stored ones stay as they are.
                    (v, values, at) =>
                    {
                        var col = donor.colors[v];
                        values[at] = Mathf.Clamp01(col.r); values[at + 1] = Mathf.Clamp01(col.g);
                        values[at + 2] = Mathf.Clamp01(col.b); values[at + 3] = Mathf.Clamp01(col.a);
                    },
                    (values, changed) => FbxLayerChannels.WriteColor(mesh, topology, values, changed));
                if (colorCorners > 0) channelsWritten.Add("vertex colours");
                written += colorCorners;
            }
            if (donor.normals != null || donor.tangents != null)
                written += WriteShading(mesh, donor, tag, topology, cornerToVertex, unsmoothed, channelsWritten);
            return written;
        }

        // Normals and the tangent frame are directions in the mesh's space: written values go
        // back into the file through the map fitted to the mesh's import (FbxSpaceFit), and
        // corners whose value the import already has keep their stored doubles.
        static int WriteShading(Autodesk.Fbx.FbxMesh mesh, Donor donor, Tagged tag, FbxLayerChannels.Topology topology,
            int[] cornerToVertex, bool unsmoothed, HashSet<string> channelsWritten)
        {
            // Fitted only when a corner changed: a mesh whose shading matches the file needs no map.
            FbxSpaceFit fitted = null;
            var source = mesh;
            FbxSpaceFit Fit() => fitted ?? (fitted = FbxStructureWrite.FitOf(source, tag, donor.name).fit);
            int written = 0;
            if (donor.normals != null)
            {
                bool exists = FbxLayerChannels.NormalElement(mesh) != null;
                bool comparable = exists && tag.normals != null;
                int corners = WriteChannel(donor.name, "normals", topology, cornerToVertex, !exists, 3,
                    (c, v) => (exists && !SelectedShadingCorner(donor, c)) || (comparable && tag.normals[c].Equals(donor.normals[v])),
                    (v, values, at) =>
                    {
                        var n = donor.normals[v];
                        Fit().NormalToFbx(n.x, n.y, n.z, out values[at], out values[at + 1], out values[at + 2]);
                    },
                    (values, changed) => FbxLayerChannels.WriteNormals(mesh, topology, values, changed, unsmoothed));
                if (corners > 0) channelsWritten.Add("normals");
                written += corners;
            }
            // A file without a tangent frame lets Unity compute one from the new normals; only
            // tangents that changed themselves give it one.
            bool frameExists = FbxLayerChannels.TangentElement(mesh) != null && FbxLayerChannels.BinormalElement(mesh) != null;
            if (donor.tangents != null && (frameExists || !donor.tangentsFollowNormals))
            {
                bool exists = frameExists;
                bool comparable = exists && tag.tangents != null;
                // Tangents left out of the save keep the file's own (as the import read them).
                bool keep = donor.keepFileTangents && comparable;
                // Unity keeps a tangent and the bitangent's sign; the file keeps both vectors:
                // binormal = cross(normal, tangent) · w, both mapped as directions.
                int corners = WriteChannel(donor.name, "tangents", topology, cornerToVertex, !exists, 6,
                    (c, v) => (exists && !SelectedShadingCorner(donor, c)) || (comparable && (keep || tag.tangents[c].Equals(donor.tangents[v]))
                        && (donor.normals == null || (tag.normals != null && tag.normals[c].Equals(donor.frameNormals[v])))),
                    (v, values, at) =>
                    {
                        var t = keep ? tag.tangents[at / 6] : donor.tangents[v];
                        // The frame is built on the normal the file will hold: the written one, or
                        // (normals not written) the corner's stored normal as the import read it.
                        var normal = donor.normals == null && tag.normals != null ? tag.normals[at / 6] : donor.frameNormals[v];
                        var b = Vector3.Cross(normal, new Vector3(t.x, t.y, t.z)) * t.w;
                        var map = Fit();
                        map.DirectionToFbx(t.x, t.y, t.z, out values[at], out values[at + 1], out values[at + 2]);
                        map.DirectionToFbx(b.x, b.y, b.z, out values[at + 3], out values[at + 4], out values[at + 5]);
                    },
                    (values, changed) =>
                    {
                        var tangents = new double[changed.Length * 3];
                        var binormals = new double[changed.Length * 3];
                        for (int c = 0; c < changed.Length; c++)
                        {
                            Array.Copy(values, c * 6, tangents, c * 3, 3);
                            Array.Copy(values, c * 6 + 3, binormals, c * 3, 3);
                        }
                        return FbxLayerChannels.WriteTangentFrame(mesh, topology, tangents, binormals, changed);
                    });
                if (corners > 0) channelsWritten.Add("tangents");
                written += corners;
            }
            return written;
        }

        // Every FBX corner's vertex in the working mesh; refuses what the file cannot hold.
        static bool SelectedShadingCorner(Donor donor, int corner) => donor.selectedCorners == null || donor.selectedCorners[corner];

        static void PrepareShadingSelection(Donor donor, Tagged tag, int corners)
        {
            if (donor.shadingSubmeshes == null) return;
            if (tag.submeshCorners.Length != donor.shadingSubmeshes.Length)
                throw new InvalidOperationException($"'{donor.name}': FBX material/submesh layout differs from the captured foliage selection.");
            donor.selectedCorners = new bool[corners];
            donor.cornerSubmeshes = Enumerable.Repeat(-1, corners).ToArray();
            for (int s = 0; s < tag.submeshCorners.Length; s++)
            {
                foreach (int c in tag.submeshCorners[s])
                {
                    if (c < 0 || c >= corners || (donor.cornerSubmeshes[c] >= 0 && donor.cornerSubmeshes[c] != s))
                        throw new InvalidOperationException($"'{donor.name}': ambiguous FBX material corner; nothing was written.");
                    donor.cornerSubmeshes[c] = s;
                    donor.selectedCorners[c] = donor.shadingSubmeshes[s];
                }
            }
        }

        static int PrepareSeamSplits(FbxSourceDocument document, List<Donor> donors, Dictionary<string, Tagged> tagged)
        {
            var plans = new Dictionary<int, FbxPolygonSplit>();
            var expanded = new List<(Tagged tag, FbxPolygonSplit plan, int cornerCount)>();
            foreach (var donor in donors)
            {
                if (!donor.HasValues || !tagged.TryGetValue(donor.name, out var tag) || tag.ambiguous) continue;
                var mesh = document.Meshes[tag.ordinal];
                var topology = new FbxLayerChannels.Topology(mesh);
                var match = MatchCorners(donor, tag, topology);
                if (match.conflicts == 0 || match.unresolved > 0) continue;
                var plan = FbxPolygonSplit.Plan(topology.polygonSizes, match.conflictingCorners, tag.TriangleCorners());
                if (plan.splitCount == 0) continue;
                if (!plans.TryAdd(tag.ordinal, plan))
                    throw new InvalidOperationException($"FBX mesh '{mesh.GetName()}' has several conflicting donors.");
                expanded.Add((tag, plan, topology.CornerCount));
            }
            if (plans.Count == 0) return 0;
            var names = document.Meshes.Select(m => m.GetName()).ToArray();
            document.SplitPolygons(plans);
            if (!names.SequenceEqual(document.Meshes.Select(m => m.GetName())))
                throw new InvalidOperationException("FBX mesh order changed during the seam split; nothing was written.");
            foreach (var item in expanded) ExpandTag(item.tag, item.plan, item.cornerCount);
            return plans.Values.Sum(p => p.splitCount);
        }

        internal static void ExpandTag(Tagged tag, FbxPolygonSplit plan, int cornerCount)
        {
            var positions = Enumerable.Repeat(float.NaN, cornerCount * 3).ToArray();
            Array.Copy(tag.cornerPositions, positions, tag.cornerPositions.Length);
            tag.cornerPositions = plan.Expand(positions, 3);
            for (int channel = 0; channel < tag.uvs.Length; ++channel) tag.uvs[channel] = plan.Expand(tag.uvs[channel]);
            tag.colors = plan.Expand(tag.colors); tag.colors32 = plan.Expand(tag.colors32);
            tag.normals = plan.Expand(tag.normals); tag.tangents = plan.Expand(tag.tangents);
            var firstCopy = FirstCornerCopies(plan, cornerCount);
            var splitTriangles = SplitTriangleCorners(plan);
            for (int s = 0; s < tag.submeshCorners.Length; ++s)
            {
                tag.submeshCorners[s] = ExpandSubmeshCorners(tag.submeshCorners[s], tag.submeshFaceSizes[s], plan, firstCopy, splitTriangles);
                tag.submeshFaceSizes[s] = 3;
            }
        }

        static int[] FirstCornerCopies(FbxPolygonSplit plan, int count)
        {
            var first = new int[count]; Array.Fill(first, -1);
            for (int c = 0; c < plan.corners.Length; ++c)
                if (first[plan.corners[c]] < 0) first[plan.corners[c]] = c;
            return first;
        }

        static Dictionary<(int,int,int),int[]> SplitTriangleCorners(FbxPolygonSplit plan)
        {
            var triangles = new Dictionary<(int,int,int),int[]>(); int offset = 0;
            foreach (int size in plan.sizes)
            {
                if (size == 3)
                {
                    var triangle = Enumerable.Range(offset, 3).ToArray();
                    var old = triangle.Select(c => plan.corners[c]).OrderBy(c => c).ToArray();
                    triangles.Add((old[0], old[1], old[2]), triangle);
                }
                offset += size;
            }
            return triangles;
        }

        /// <summary>Remaps submesh triangles to split polygon corners, triangulating quads and preserving each triangle's corner order.</summary>
        static int[] ExpandSubmeshCorners(int[] original, int size, FbxPolygonSplit plan, int[] first,
            Dictionary<(int,int,int),int[]> splitTriangles)
        {
            var triangles = size == 4
                ? Enumerable.Range(0, original.Length / 4).SelectMany(f => new[] { original[f*4], original[f*4+1], original[f*4+2],
                    original[f*4], original[f*4+2], original[f*4+3] }).ToArray() : original;
            var mapped = new int[triangles.Length];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                var old = new[] { triangles[t], triangles[t+1], triangles[t+2] }; var sorted = old.OrderBy(c => c).ToArray();
                if (splitTriangles.TryGetValue((sorted[0], sorted[1], sorted[2]), out var triangle))
                    for (int k = 0; k < 3; ++k) mapped[t+k] = triangle.Single(c => plan.corners[c] == old[k]);
                else for (int k = 0; k < 3; ++k) mapped[t+k] = old[k] < 0 ? old[k] : first[old[k]];
            }
            return mapped;
        }

        /// <summary>Pairs donor vertices with FBX corners using preserved attributes and the source import's winding.</summary>
        static FbxCornerMatch.Result MatchCorners(Donor donor, Tagged tag, in FbxLayerChannels.Topology topology)
            => FbxCornerMatch.Match(topology.polygonSizes, CornerPositions(donor, tag, topology), donor.positions, donor.faces, donor.faceSizes,
                (c, v) => SignatureMatches(donor, tag, c, v),
                (a, b) => SameWrittenValues(donor, a, b), tag.TriangleCorners());

        /// <summary>Returns working-mesh vertices for FBX corners, refusing conflicts or unmatched faces and retaining missing degenerate corners as -1.</summary>
        static int[] PairCorners(Donor donor, Tagged tag, in FbxLayerChannels.Topology topology)
        {
            var match = MatchCorners(donor, tag, topology);
            if (match.conflicts > 0)
                throw new CornerPairingException($"{match.conflicts} corner(s) get different values from different faces",
                    $"'{donor.name}': {match.conflicts} FBX corner(s) get different values from different triangles of the same polygon " +
                    "(a UV seam, colour edge or hard edge runs inside a polygon). The FBX polygon cannot hold that without being split; nothing was written.");
            if (match.unresolved > 0)
                throw new CornerPairingException($"{match.unresolved} corner(s) have no face in its import",
                    $"'{donor.name}': {match.unresolved} FBX corner(s) have no face in the working mesh — its geometry differs from the file " +
                    "(simplified or edited faces). Channels alone cannot carry that; save with a rebuild. Nothing was written.");
            if (match.missing > 0)
                UvtLog.Warn($"[FBX Export] '{donor.name}': {match.missing} corner(s) of degenerate polygons have no vertex in Unity's import; they keep their stored values.");
            if (match.zeroAreaChoices > 0)
                UvtLog.Info($"[FBX Export] '{donor.name}': {match.zeroAreaChoices} corner(s) of zero-area polygons (fewer than three distinct positions) " +
                    "get different values from their faces; they take one of them, as such polygons cover no area.");
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
            if (donor.cornerSubmeshes != null && (donor.cornerSubmeshes[corner] < 0 ||
                !donor.submeshVertices[donor.cornerSubmeshes[corner]].Contains(vertex))) return false;
            for (int ch = 0; ch < 8; ch++)
            {
                if (donor.signatureUvs[ch] == null || tag.uvs[ch] == null) continue;
                if (!donor.signatureUvs[ch][vertex].Equals(tag.uvs[ch][corner])) return false;
            }
            if (donor.signatureColors32 != null && tag.colors32 != null && corner < tag.colors32.Length)
                return Same(donor.signatureColors32[vertex], tag.colors32[corner]);
            if (donor.signatureColors != null && tag.colors != null && corner < tag.colors.Length)
                return donor.signatureColors[vertex].Equals(tag.colors[corner]);
            return true;
        }

        static bool SameWrittenValues(Donor donor, int a, int b)
        {
            for (int ch = 0; ch < 8; ch++)
                if (donor.uvs[ch] != null && !donor.uvs[ch][a].Equals(donor.uvs[ch][b])) return false;
            if (donor.normals != null && !donor.normals[a].Equals(donor.normals[b])) return false;
            if (donor.tangents != null && !donor.tangents[a].Equals(donor.tangents[b])) return false;
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

        // The leading FBX UV sets the intent covers; sets go in order, so the first one it does
        // not (or a UV1 the import regenerates anyway) ends them.
        static int EditableUvSets(FbxExportIntent intent, bool swapUv, bool uv1Generated)
        {
            int sets = 0;
            while (sets < 8)
            {
                int channel = FbxUvSet(sets, swapUv);
                if (!intent.IncludesUv(channel) || (channel == 1 && uv1Generated)) break;
                sets++;
            }
            return sets;
        }

        // Runs set (importer changes written to the .meta); false, reported, when they cannot be.
        static bool SetImportSettings(string fbxPath, Action set)
        {
            try
            {
                set();
                return true;
            }
            catch (Exception ex)
            {
                UvtLog.Error($"[FBX Export] '{fbxPath}' was written, but its import settings could not be set: {ex.Message}. " +
                    "The edits stay unsaved; save again once the cause is fixed.");
                UvtLog.Verbose(ex.ToString());
                return false;
            }
        }

        // Unity reports a .meta it cannot write without throwing: an importer about to change is
        // checked first, before anything on it changes.
        static void RequireWritableSettings(string fbxPath)
        {
            string meta = Path.GetFullPath(AssetDatabase.GetTextMetaFilePathFromAssetPath(fbxPath));
            if (File.Exists(meta) && (File.GetAttributes(meta) & FileAttributes.ReadOnly) != 0)
                throw new IOException($"'{meta}' is read-only");
        }

        // generateSecondaryUV regenerates Unity UV channel 1 on import and would replace the
        // channel just written; switch it off for this model (the one importer setting the
        // write needs).
        static void KeepWrittenUv1(string sourceFbxPath)
        {
            var importer = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            if (importer == null || !importer.generateSecondaryUV) return;
            RequireWritableSettings(sourceFbxPath);
            importer.generateSecondaryUV = false;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(sourceFbxPath);
            UvtLog.Info($"[FBX Export] 'Generate Lightmap UVs' switched off on '{sourceFbxPath}' so the written UV1 is used.");
        }

        // The importer's material remaps, by FBX material name.
        static Dictionary<string, Material> MaterialRemaps(ModelImporter importer)
        {
            var remaps = new Dictionary<string, Material>(StringComparer.Ordinal);
            if (importer == null) return remaps;
            foreach (var kv in importer.GetExternalObjectMap())
                if (kv.Key.type == typeof(Material) && kv.Value is Material material) remaps[kv.Key.name] = material;
            return remaps;
        }

        // A material the save put into a node slot maps, on import, to the asset the renderer
        // shows: the slot holds an FBX material of that name, the importer maps the name.
        static void MapMaterials(string fbxPath, Dictionary<string, Material> remaps)
        {
            if (remaps.Count == 0 || !(AssetImporter.GetAtPath(fbxPath) is ModelImporter importer)) return;
            RequireWritableSettings(fbxPath);
            foreach (var kv in remaps) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(fbxPath);
            UvtLog.Info($"[FBX Export] '{fbxPath}': {string.Join(", ", remaps.Select(kv => $"'{kv.Key}' → {kv.Value.name}"))} mapped on the importer.");
        }

        // A variant is a new file: import it the way its source is imported, so its meshes
        // carry the same names and layout as the source's — except a written UV1 is kept.
        static void ConfigureLikeSource(string sourceFbxPath, string variantFbxPath, bool uv1Changed, Dictionary<string, Material> remaps)
        {
            var source = AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter;
            var variant = AssetImporter.GetAtPath(variantFbxPath) as ModelImporter;
            if (source == null || variant == null) return;
            RequireWritableSettings(variantFbxPath);
            new Preset(source).ApplyTo(variant);
            // The materials the save wrote into the variant map to their assets like the source's do.
            foreach (var kv in remaps) variant.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            // The variant's UV1 is the one just written (or removed): Unity must not regenerate it.
            if (uv1Changed) variant.generateSecondaryUV = false;
            EditorUtility.SetDirty(variant);
            AssetDatabase.WriteImportSettingsIfDirty(variantFbxPath);
        }

        // The variant imported with its source's settings; a sidecar at the variant's path must
        // not replay over a UV1 just written.
        static void ImportWrittenAsset(string path, FbxExport.ShadingSelection selection)
        {
            if (selection == null) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            else selection.importAsset(path, ImportAssetOptions.ForceUpdate);
        }

        static void ReimportVariant(string variantFbxPath, bool uv1Changed, FbxExport.ShadingSelection selection)
        {
            if (uv1Changed) Uv2AssetPostprocessor.fbxOverwritePaths.Add(variantFbxPath);
            try { ImportWrittenAsset(variantFbxPath, selection); }
            finally { Uv2AssetPostprocessor.fbxOverwritePaths.Remove(variantFbxPath); }
        }
    }
}
#endif
