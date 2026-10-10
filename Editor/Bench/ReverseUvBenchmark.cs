using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Repeatable coarse-to-fine trials using frozen forward-transfer inputs.
    /// Runs both overlap policies on clones, without changing scene or model assets.</summary>
    internal static class ReverseUvBenchmark
    {
        [Serializable] internal sealed class Trial
        {
            public int pair; public string source, target;
            public bool allowOverlap, accepted;
            public string error, audit, failureStage;
            public ReverseUvTransfer.Report result;
        }
        [Serializable] internal sealed class Summary
        {
            public string manifest;
            public int accepted, refused;
            public List<Trial> trials = new List<Trial>();
        }

        [MenuItem("Tools/Mesh Lab/Diagnostics/Reverse UV — benchmark last capture")]
        static void RunLastCapture() => _ = RunLastCaptureAsync();

        static async Task RunLastCaptureAsync()
        {
            if (UvProgress.IsActive) { UvtLog.Warn("[ReverseUV] Wait for the active operation to finish."); return; }
            UvProgress.Begin("Reverse UV frozen benchmark");
            try
            {
                var summary = await Run(TransferCaseCapture.LastManifest, Path.Combine(SweepRunner.ReportsRoot(),
                    "reverse_bench_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")));
                UvtLog.Info($"[ReverseUV] Benchmark: {summary.accepted} accepted, {summary.refused} refused.");
                UvProgress.End();
            }
            catch (Exception ex) { UvtLog.Error("[ReverseUV] " + ex.Message); UvProgress.Fail(ex.Message); }
        }

        internal static async Task<Summary> Run(string manifestPath, string outputDirectory)
        {
            var file = new FileInfo(manifestPath);
            if (!file.Exists || file.Length > TransferCaseCapture.MaxManifestBytes)
                throw new InvalidDataException("Choose an existing bounded transfer capture first.");
            var manifest = JsonUtility.FromJson<TransferCaseCapture.Manifest>(await File.ReadAllTextAsync(file.FullName));
            if (manifest == null || manifest.pairs == null || manifest.pairs.Count == 0 || manifest.pairs.Count > TransferCaseCapture.MaxPairs)
                throw new InvalidDataException("Capture has no supported transfer pairs.");
            Directory.CreateDirectory(outputDirectory);
            var summary = new Summary { manifest = file.FullName };
            var sourceFrames = await ReadSourceFrames(manifest, file.DirectoryName);
            var previousOutput = BenchmarkRecorder.OutputDirectoryOverride;
            try
            {
                BenchmarkRecorder.OutputDirectoryOverride = outputDirectory;
                foreach (var pair in manifest.pairs)
                    foreach (bool overlap in new[] { false, true })
                    {
                        var trial = new Trial { pair = pair.index, source = pair.source, target = pair.target, allowOverlap = overlap };
                        summary.trials.Add(trial);
                        Mesh coarse = null, fine = null;
                        trial.failureStage = "snapshot";
                        try
                        {
                            // A forward capture's target is the coarse mesh. Prepare
                            // it from geometry rather than copying failed forward UV2.
                            coarse = TransferCaseReplay.Load(file.DirectoryName, pair.targetMesh);
                            fine = TransferCaseReplay.Load(file.DirectoryName, pair.sourceMesh);
                            trial.failureStage = "cleanup";
                            var sources = new[] {
                                new ReverseUvTransfer.Level { lod = 1, inputs = new[] { new ReverseUvTransfer.Input { mesh = coarse, key = pair.target, toWorld = pair.localToWorld } } },
                                new ReverseUvTransfer.Level { lod = 0, inputs = new[] { new ReverseUvTransfer.Input { mesh = fine, key = pair.source, toWorld = SourceTransform(pair, sourceFrames) } } }
                            };
                            trial.failureStage = "seed/projection";
                            using var prepared = await ReverseUvJunctionTrial.Build(sources, new ReverseUvTransfer.Options {
                                seedResolution = 256, projectionReach = .05f, preserveProjectedOverlap = overlap }, true, 0, true);
                            var levels = prepared.inputs.levels; var result = prepared.result;
                            trial.failureStage = "audit";
                            trial.audit = ReverseUvAudit.Write(result, levels); trial.result = result.report;
                            trial.accepted = true; trial.failureStage = null; ++summary.accepted;
                        }
                        catch (Exception ex) { trial.error = ex.Message; ++summary.refused; }
                        finally
                        {
                            if (coarse) UnityEngine.Object.DestroyImmediate(coarse);
                            if (fine) UnityEngine.Object.DestroyImmediate(fine);
                        }
                    }
                await File.WriteAllTextAsync(Path.Combine(outputDirectory,"summary.json"),JsonUtility.ToJson(summary,true));
                return summary;
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = previousOutput; }
        }

        static async Task<List<TransferCaseCapture.MeshState>> ReadSourceFrames(TransferCaseCapture.Manifest manifest, string root)
        {
            var frames = new List<TransferCaseCapture.MeshState>();
            if (manifest.pairs.All(p => p.hasSourceTransform)) return frames;
            foreach (var stage in manifest.stages)
            {
                var data = stage;
                if (!string.IsNullOrEmpty(stage.details))
                {
                    var bytes = await Task.Run(() => TransferCaseReplay.Verify(root, stage.details, "details", ".json"));
                    data = JsonUtility.FromJson<TransferCaseCapture.Stage>(System.Text.Encoding.UTF8.GetString(bytes));
                }
                if (data?.meshes != null) frames.AddRange(data.meshes);
            }
            return frames;
        }

        internal static Matrix4x4 SourceTransform(TransferCaseCapture.Pair pair, List<TransferCaseCapture.MeshState> frames)
        {
            if (pair.hasSourceTransform) return pair.sourceLocalToWorld;
            var candidates = frames.Where(s => s.renderer == pair.source &&
                (s.working == pair.sourceMesh || s.repacked == pair.sourceMesh || s.transferred == pair.sourceMesh))
                .Select(s => s.localToWorld).Distinct().ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("Old capture has no unique source renderer transform; reverse benchmark requires a fresh capture.");
            return candidates[0];
        }
    }
}
