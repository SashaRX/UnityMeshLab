using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Opt-in, exact transfer inputs. No scene/assets are changed by capture or replay.</summary>
    internal sealed class TransferCaseCapture
    {
        internal const long MaxManifestBytes = 64L * 1024 * 1024;
        internal const int MaxPairs = 10000;
        static string LastPathKey => "MeshLab.TransferCapture.LastManifest." +
            TransferMeshSnapshot.Hash(System.Text.Encoding.UTF8.GetBytes(Application.dataPath));
        [Serializable] internal sealed class Setting { public string owner, name, value; }
        [Serializable] internal sealed class MeshState
        {
            public string renderer, group, importedAsset, working, repacked, transferred;
            public int lod; public bool welded, edgeWelded, symmetrySplit;
            public uint atlasWidth, atlasHeight, nativePackedWidth, nativePackedHeight;
            public Matrix4x4 localToWorld;
            public SourceTextureUvMetric textureMetric;
            public TransferUvQuality uv0, textureUv0, uv2;
        }
        [Serializable] internal sealed class Stage
        {
            public string name, details;
            public List<Setting> settings = new List<Setting>();
            public List<MeshState> meshes = new List<MeshState>();
        }
        [Serializable] internal sealed class Pair
        {
            public int index, targetLod, atlasWidth, atlasHeight;
            public string source, target, group, sourceMesh, targetMesh, outputMesh, baselineUvHash, status = "pending";
            public string details;
            public bool clamp;
            public Matrix4x4 localToWorld;
            public List<GroupedShellTransfer.OverlapSourceHint> overlapHints;
            public List<GroupedShellTransfer.CrossLodMatchHint> matchHints;
            public GroupedShellTransfer.TransferResult result;
            public TransferMatchTrace trace = new TransferMatchTrace();
            public TransferUvQuality quality;
            public Validation validation;
        }
        [Serializable] internal sealed class PairDetails
        {
            public List<GroupedShellTransfer.OverlapSourceHint> overlapHints;
            public List<GroupedShellTransfer.CrossLodMatchHint> matchHints;
            public GroupedShellTransfer.TransferResult result;
            public TransferMatchTrace trace;
            public TransferUvQuality quality;
            public Validation validation;
        }
        [Serializable] internal sealed class Validation
        {
            public int inverted, stretched, zeroArea, outOfBounds, differentSourceOverlapPairs, sameSourceOverlapPairs, badDensity;
            public TransferValidator.TriIssue[] perTriangle;
        }
        [Serializable] internal sealed class Manifest
        {
            public int schema = 2;
            public string createdUtc, unityVersion, packageName, packageVersion, gitSha, gitBranch, packagePath;
            public bool gitDirty;
            public string run, status = "recording", error;
            public List<Stage> stages = new List<Stage>();
            public List<Pair> pairs = new List<Pair>();
        }

        internal readonly string Folder;
        internal readonly Manifest Data;
        readonly UvToolContext context;
        bool failed;
        internal static string LastManifest => EditorPrefs.GetString(LastPathKey, "");

        TransferCaseCapture(UvToolContext ctx, string run)
        {
            context = ctx;
            Folder = Path.Combine(SweepRunner.ReportsRoot(), "transfer_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(Folder, "meshes"));
            Directory.CreateDirectory(Path.Combine(Folder, "details"));
            var provenance = BenchmarkSweep.ResolvePackageProvenance();
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TransferCaseCapture).Assembly);
            Data = new Manifest { createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion, run = run, packageName = provenance.pkgName,
                packageVersion = provenance.pkgVersion, gitSha = provenance.gitSha, gitBranch = provenance.gitBranch,
                gitDirty = provenance.gitDirty, packagePath = info?.resolvedPath ?? "" };
            Save();
            EditorPrefs.SetString(LastPathKey, Path.Combine(Folder, "manifest.json"));
        }

        internal static TransferCaseCapture Begin(UvToolContext ctx, object workflow, string run)
        {
            if (ctx.DiagnosticCapture != null || !ctx.CaptureNextTransfer) return null;
            ctx.CaptureNextTransfer = false;
            try {
                var capture = new TransferCaseCapture(ctx, run);
                ctx.DiagnosticCapture = capture;
                capture.StageSafe(workflow, "before-run");
                UvtLog.Info(UvtLog.Category.Benchmark, "[TransferCapture] Recording to " + capture.Folder);
                return capture;
            }
            catch (Exception error) { UvtLog.Warn(UvtLog.Category.Benchmark, "[TransferCapture] Cannot start: " + error.Message); return null; }
        }

        internal void StageSafe(object workflow, string name)
        {
            Safe(() => {
                var stage = new Stage { name = name };
                ReadSettings(stage.settings, context, "context");
                if (workflow is UvTransferWorkflow uvWorkflow) uvWorkflow.AppendDiagnosticSettings(stage.settings);
                stage.settings.Add(new Setting { owner = "context", name = "PipeSettings", value = JsonUtility.ToJson(context.PipeSettings) });
                foreach (var entry in context.MeshEntries) {
                    if (!entry.include || !entry.originalMesh) continue;
                    stage.meshes.Add(CaptureMeshState(entry));
                }
                RecordStage(stage);
            });
        }

        MeshState CaptureMeshState(MeshEntry entry)
        {
            var metric = SourceTextureUvMetric.Resolve(entry.renderer, entry.previewTexture, entry.originalMesh);
            var matrix = entry.renderer ? entry.renderer.localToWorldMatrix : Matrix4x4.identity;
            var state = new MeshState { renderer = entry.renderer ? entry.renderer.name : entry.originalMesh.name,
                group = entry.meshGroupKey, lod = entry.lodIndex, importedAsset = AssetDatabase.GetAssetPath(entry.fbxMesh),
                working = StoreMesh(entry.originalMesh), repacked = StoreMesh(entry.repackedMesh), transferred = StoreMesh(entry.transferredMesh),
                welded = entry.wasWelded, edgeWelded = entry.wasEdgeWelded, symmetrySplit = entry.wasSymmetrySplit,
                atlasWidth = entry.repackedAtlasWidth, atlasHeight = entry.repackedAtlasHeight,
                nativePackedWidth = entry.repackedMesh ? entry.diagnosticPackedAtlasWidth : 0,
                nativePackedHeight = entry.repackedMesh ? entry.diagnosticPackedAtlasHeight : 0,
                localToWorld = matrix, textureMetric = metric };
            Mesh readable = MeshAccess.ReadableCopy(entry.originalMesh);
            try {
                state.uv0 = TransferUvQuality.Measure(readable, readable.uv, Vector2.one, matrix);
                state.textureUv0 = TransferUvQuality.Measure(readable, readable.uv, metric.uvScale, matrix);
            }
            finally { UnityEngine.Object.DestroyImmediate(readable); }
            var output = entry.transferredMesh ? entry.transferredMesh : entry.repackedMesh;
            if (output) state.uv2 = TransferUvQuality.Measure(output, output.uv2, Vector2.one, matrix);
            return state;
        }

        internal void RecordStage(Stage stage)
        {
            stage.details = StoreDetails(stage);
            Data.stages.Add(stage); Save();
        }

        internal Pair BeforePair(MeshEntry source, MeshEntry target, Mesh sourceMesh, Mesh targetMesh,
            List<GroupedShellTransfer.OverlapSourceHint> overlapHints, List<GroupedShellTransfer.CrossLodMatchHint> matchHints)
        {
            Pair pair = null;
            Safe(() => {
                if (Data.pairs.Count >= MaxPairs) throw new InvalidDataException("Transfer capture exceeds the replay pair limit.");
                pair = new Pair { index = Data.pairs.Count, source = source.renderer ? source.renderer.name : sourceMesh.name,
                    target = target.renderer ? target.renderer.name : targetMesh.name, targetLod = target.lodIndex, group = target.meshGroupKey,
                    sourceMesh = StoreMesh(sourceMesh), targetMesh = StoreMesh(targetMesh), clamp = context.ClampLightmapToUnit,
                    atlasWidth = (int)source.repackedAtlasWidth, atlasHeight = (int)source.repackedAtlasHeight,
                    localToWorld = target.renderer ? target.renderer.localToWorldMatrix : Matrix4x4.identity,
                    overlapHints = overlapHints == null || overlapHints.Count == 0 ? null : new List<GroupedShellTransfer.OverlapSourceHint>(overlapHints),
                    matchHints = matchHints == null || matchHints.Count == 0 ? null : new List<GroupedShellTransfer.CrossLodMatchHint>(matchHints) };
                StorePairDetails(pair);
                Data.pairs.Add(pair); Save();
            });
            return failed ? null : pair;
        }

        internal void AfterPair(Pair pair, MeshEntry entry, GroupedShellTransfer.TransferResult result)
        {
            if (pair == null) return;
            Safe(() => {
                pair.result = result;
                pair.baselineUvHash = TransferMeshSnapshot.UvHash(result.uv2);
                if (result.uv2 != null && entry.transferredMesh) {
                    pair.outputMesh = StoreMesh(entry.transferredMesh);
                    pair.quality = TransferUvQuality.Measure(entry.transferredMesh, result.uv2, Vector2.one, pair.localToWorld);
                    var validation = entry.validationReport;
                    if (validation != null) pair.validation = new Validation {
                        inverted = validation.invertedCount, stretched = validation.stretchedCount, zeroArea = validation.zeroAreaCount,
                        outOfBounds = validation.oobCount, differentSourceOverlapPairs = validation.overlapShellPairs,
                        sameSourceOverlapPairs = validation.overlapSameSrcPairs, badDensity = validation.texelDensityBadCount,
                        perTriangle = validation.perTriangle == null ? null : (TransferValidator.TriIssue[])validation.perTriangle.Clone()
                    };
                }
                StorePairDetails(pair);
                pair.status = result.uv2 == null ? "failed" : "complete";
                Save();
            });
        }

        internal void Finish(bool complete)
        {
            if (failed) Data.status = "capture-failed";
            else if (complete) Data.status = "complete";
            else Data.status = "aborted";
            try { Save(); }
            catch (Exception error) {
                failed = true; Data.status = "capture-failed"; Data.error = error.ToString();
                UvtLog.Warn(UvtLog.Category.Benchmark, "[TransferCapture] Cannot finish: " + error.Message);
            }
            finally { if (context.DiagnosticCapture == this) context.DiagnosticCapture = null; }
        }

        internal static void CaptureCurrent(UvToolContext ctx)
        {
            if (UvProgress.IsActive || ctx.DiagnosticCapture != null) throw new InvalidOperationException("Wait for the active operation to finish.");
            var capture = new TransferCaseCapture(ctx, "Current snapshot (no historical transfer inputs)");
            capture.StageSafe(null, "current"); capture.Finish(true);
            if (capture.Data.status == "complete")
                UvtLog.Info(UvtLog.Category.Benchmark, "[TransferCapture] Snapshot: " + capture.Folder + ". Arm the next run to capture exact hints and replay inputs.");
            else UvtLog.Warn(UvtLog.Category.Benchmark, "[TransferCapture] Snapshot incomplete: " + capture.Folder + ". " + capture.Data.error);
        }

        string StoreMesh(Mesh mesh)
        {
            if (!mesh) return "";
            Mesh readable = mesh.isReadable ? mesh : MeshAccess.ReadableCopy(mesh);
            try {
                byte[] bytes = TransferMeshSnapshot.Capture(readable);
                string hash = TransferMeshSnapshot.Hash(bytes);
                string path = Path.Combine(Folder, "meshes", hash + ".bin");
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                return hash;
            }
            finally { if (readable != mesh) UnityEngine.Object.DestroyImmediate(readable); }
        }

        void Safe(Action action)
        {
            if (failed) return;
            try { action(); }
            catch (Exception error) {
                failed = true; Data.error = error.ToString();
                UvtLog.Warn(UvtLog.Category.Benchmark, "[TransferCapture] Capture incomplete: " + error.Message);
            }
        }

        void Save()
        {
            string path = Path.Combine(Folder, "manifest.json"), temp = path + ".tmp";
            // Keep metadata bounded independently of vertex/face counts and pipeline attempts.
            var compact = new Manifest { schema = Data.schema, createdUtc = Data.createdUtc,
                unityVersion = Data.unityVersion, packageName = Data.packageName, packageVersion = Data.packageVersion,
                gitSha = Data.gitSha, gitBranch = Data.gitBranch, gitDirty = Data.gitDirty, packagePath = Data.packagePath,
                run = Data.run, status = Data.status, error = Data.error };
            foreach (var stage in Data.stages) compact.stages.Add(new Stage { name = stage.name, details = stage.details });
            foreach (var pair in Data.pairs) compact.pairs.Add(new Pair { index = pair.index, targetLod = pair.targetLod,
                atlasWidth = pair.atlasWidth, atlasHeight = pair.atlasHeight, source = pair.source, target = pair.target,
                group = pair.group, sourceMesh = pair.sourceMesh, targetMesh = pair.targetMesh, outputMesh = pair.outputMesh,
                baselineUvHash = pair.baselineUvHash, status = pair.status, clamp = pair.clamp,
                localToWorld = pair.localToWorld, details = pair.details, trace = null });
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(compact, true));
            if (bytes.LongLength > MaxManifestBytes) throw new InvalidDataException("Transfer manifest exceeds the replay size limit.");
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        void StorePairDetails(Pair pair)
        {
            pair.details = StoreDetails(new PairDetails { overlapHints = pair.overlapHints, matchHints = pair.matchHints,
                result = pair.result, trace = pair.trace, quality = pair.quality, validation = pair.validation });
        }

        string StoreDetails(object details)
        {
            byte[] bytes = EncodeDetails(details);
            string hash = TransferMeshSnapshot.Hash(bytes);
            string path = Path.Combine(Folder, "details", hash + ".json");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return hash;
        }

        internal static async System.Threading.Tasks.Task<string> StoreDetailsAsync(string folder, object details)
        {
            byte[] bytes = EncodeDetails(details);
            string hash = TransferMeshSnapshot.Hash(bytes);
            string directory = Path.Combine(folder, "details");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, hash + ".json");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            return hash;
        }

        static byte[] EncodeDetails(object details)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(details, false));
            if (bytes.LongLength > TransferMeshSnapshot.MaxFileBytes) throw new InvalidDataException("Transfer diagnostic payload exceeds the size limit.");
            return bytes;
        }

        internal static T ReadDetails<T>(string root, string hash)
            => JsonUtility.FromJson<T>(System.Text.Encoding.UTF8.GetString(TransferCaseReplay.Verify(root, hash, "details", ".json")));

        internal static void RestorePairDetails(string root, Pair pair)
        {
            var details = ReadDetails<PairDetails>(root, pair.details);
            if (details == null) throw new InvalidDataException("Invalid transfer pair payload.");
            pair.overlapHints = details.overlapHints; pair.matchHints = details.matchHints;
            pair.result = details.result; pair.trace = details.trace;
            pair.quality = details.quality; pair.validation = details.validation;
        }

        static void ReadSettings(List<Setting> output, object source, string owner)
        {
            if (source == null) return;
            foreach (var field in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) {
                var type = field.FieldType;
                if (!type.IsPrimitive && !type.IsEnum && type != typeof(string)) continue;
                output.Add(new Setting { owner = owner, name = field.Name,
                    value = Convert.ToString(field.GetValue(source), CultureInfo.InvariantCulture) });
            }
            output.Sort((a, b) => string.CompareOrdinal(a.owner + a.name, b.owner + b.name));
        }
    }
}
