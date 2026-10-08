using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Frozen-input comparison, independent of the tool window and the scene.</summary>
    internal static class TransferBenchmark
    {
        internal const int MaxCases = 128;
        const int MaxRows = MaxCases * 6;
        [Serializable] internal sealed class Config
        {
            public int schema = 1, repetitions = 3, warmup = 1, maxPairs = 64;
            public bool includeSynthetic = true;
            public string outputRoot;
            public string[] captures = Array.Empty<string>();
            public TransferBenchmarkAssets.Case[] assetCases = Array.Empty<TransferBenchmarkAssets.Case>();
            public string[] methods = (string[])TransferBenchmarkMethods.Names.Clone();
        }

        internal sealed class Input : IDisposable
        {
            internal string name, description, capture, sourceHash, targetHash;
            internal Mesh source, target;
            internal Vector2[] reference;
            internal bool referenceIsGroundTruth, negativeControl, clamp;
            internal int atlasWidth, atlasHeight;
            internal Matrix4x4 localToWorld = Matrix4x4.identity;
            internal List<GroupedShellTransfer.OverlapSourceHint> overlapHints;
            internal List<GroupedShellTransfer.CrossLodMatchHint> matchHints;
            public void Dispose()
            {
                if (source) UnityEngine.Object.DestroyImmediate(source);
                if (target) UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Serializable] internal sealed class Quality
        {
            public int faces, degenerateFaces, invalidFaces, stretchedFaces, overlapPairs, outOfBoundsVertices;
            public double worstAnisotropy, areaWeightedAnisotropy, overlapPairArea;
            public bool overlapScanComplete;
            internal static Quality From(TransferUvQuality q) => new Quality {
                faces = q.faces, degenerateFaces = q.degenerateFaces, invalidFaces = q.invalidFaces, stretchedFaces = q.stretchedFaces,
                overlapPairs = q.overlapPairs, outOfBoundsVertices = q.outOfBoundsVertices, worstAnisotropy = q.worstAnisotropy,
                areaWeightedAnisotropy = q.areaWeightedAnisotropy, overlapPairArea = q.overlapPairArea, overlapScanComplete = q.overlapScanComplete
            };
        }

        [Serializable] internal sealed class Row
        {
            public string name, method, capture, description, sourceHash, targetHash, uvHash, mappingHash, details, view, error;
            public bool referenceIsGroundTruth, negativeControl, deterministic = true, inputUnchanged = true, referencePass;
            public int vertices, sourceFaces, targetFaces, misses, fallbackVertices, overlapHints, matchHints, clampedVertices;
            public double minimumMilliseconds, medianMilliseconds, maximumMilliseconds, qualityMilliseconds, rmsReferenceTexels, maximumReferenceTexels;
            public double[] milliseconds;
            public Quality sourceQuality, quality;
            public int matchedShells, unmatchedShells, rejectedShells, interpolationShells, transformShells, mergedShells;
        }
        [Serializable] internal sealed class Details
        {
            public Vector2[] uv, reference;
            public Matrix4x4 localToWorld;
            public int atlasWidth, atlasHeight;
            public TransferUvQuality quality;
            public TransferMatchTrace trace;
            public GroupedShellTransfer.TransferResult result;
        }
        [Serializable] internal sealed class Report
        {
            public int schema = 1, requestedCases, completedCases;
            public string createdUtc, unityVersion, packageName, packageVersion, gitSha, gitBranch, folder, error;
            public bool gitDirty, complete, cancelled;
            public Config config;
            public List<Row> rows = new List<Row>();
            [NonSerialized] internal TransferCaseCapture.Manifest corpus;
        }
        sealed class Job { internal string name; internal Func<Input> load; }

        internal static void Validate(Config config)
        {
            if (config == null || config.schema != 1) throw new InvalidDataException("Unsupported transfer benchmark config.");
            if (config.repetitions < 2 || config.repetitions > 7 || config.warmup < 0 || config.warmup > 2)
                throw new InvalidDataException("Use 2..7 measured repetitions and 0..2 warmup runs.");
            if (config.maxPairs < 1 || config.maxPairs > MaxCases || config.captures == null || config.captures.Length > MaxCases
                || config.assetCases == null || config.assetCases.Length > MaxCases)
                throw new InvalidDataException("Transfer benchmark case limit exceeded.");
            if (config.methods == null || config.methods.Length == 0 || config.methods.Length > TransferBenchmarkMethods.Names.Length)
                throw new InvalidDataException("Select 1..6 transfer methods.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string method in config.methods)
                if (Array.IndexOf(TransferBenchmarkMethods.Names, method) < 0 || !seen.Add(method))
                    throw new InvalidDataException("Unknown or duplicate transfer method: " + method);
            if (!config.includeSynthetic && config.captures.Length == 0 && config.assetCases.Length == 0)
                throw new InvalidDataException("Select synthetic cases, imported assets or at least one capture.");
        }

        internal static async Task<Report> Run(Config config)
        {
            Validate(config);
            if (UvProgress.IsActive) throw new InvalidOperationException("Wait for the active operation to finish.");
            var provenance = BenchmarkSweep.ResolvePackageProvenance();
            var report = new Report { createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), unityVersion = Application.unityVersion,
                packageName = provenance.pkgName, packageVersion = provenance.pkgVersion, gitSha = provenance.gitSha, gitBranch = provenance.gitBranch,
                gitDirty = provenance.gitDirty, config = config, folder = NewFolder(config.outputRoot) };
            report.corpus = new TransferCaseCapture.Manifest { createdUtc = report.createdUtc, unityVersion = report.unityVersion,
                packageName = report.packageName, packageVersion = report.packageVersion, gitSha = report.gitSha, gitBranch = report.gitBranch,
                gitDirty = report.gitDirty, run = "Frozen benchmark inputs; reference semantics are recorded in comparison.json", status = "recording" };
            Directory.CreateDirectory(report.folder);
            // Production exposes last-run counters for the UI. A benchmark must not replace them.
            int iterations = GroupedShellTransfer.LastTopologyIterations, fixedCount = GroupedShellTransfer.LastTopologyFixed;
            bool cap = GroupedShellTransfer.LastTopologyCapHit;
            UvProgress.Begin("Compare UV transfer methods", cancelable: true);
            try {
                var jobs = Jobs(config); report.requestedCases = jobs.Count;
                foreach (var job in jobs) {
                    if (UvProgress.CancelRequested) break;
                    UvProgress.Report((float)report.completedCases / jobs.Count, job.name);
                    await RunCase(job, config, report);
                    if (UvProgress.CancelRequested) break;
                    ++report.completedCases;
                }
                report.cancelled = UvProgress.CancelRequested;
                report.complete = !report.cancelled && report.completedCases == report.requestedCases && report.rows.TrueForAll(
                    row => string.IsNullOrEmpty(row.error) && row.deterministic && row.inputUnchanged);
            }
            catch (OperationCanceledException) { report.cancelled = true; }
            catch (Exception error) { report.error = error.ToString(); }
            finally {
                GroupedShellTransfer.LastTopologyIterations = iterations; GroupedShellTransfer.LastTopologyFixed = fixedCount; GroupedShellTransfer.LastTopologyCapHit = cap;
                report.corpus.status = report.complete ? "complete" : report.cancelled ? "aborted" : "partial";
                try { await Save(report); }
                finally { if (report.cancelled) UvProgress.Cancel(); else UvProgress.End(); }
            }
            UvtLog.Info(UvtLog.Category.Benchmark, "[TransferBenchmark] " + Path.Combine(report.folder, "comparison.json") + " complete=" + report.complete);
            return report;
        }

        static string NewFolder(string root)
        {
            string directory = Path.GetFullPath(string.IsNullOrEmpty(root) ? SweepRunner.ReportsRoot() : root);
            string assets = Path.GetFullPath(Application.dataPath), packages = Path.Combine(Path.GetDirectoryName(assets), "Packages");
            if (Inside(directory, assets) || Inside(directory, packages)) throw new InvalidDataException("Benchmark output must be outside Assets and Packages.");
            return Path.Combine(directory, "transfer_compare_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }
        static bool Inside(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        static List<Job> Jobs(Config config)
        {
            var jobs = new List<Job>();
            if (config.includeSynthetic)
                foreach (string fixture in TransferBenchmarkFixtures.Names)
                    jobs.Add(new Job { name = fixture, load = () => TransferBenchmarkFixtures.Create(fixture) });
            foreach (string path in config.captures) {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > TransferCaseCapture.MaxManifestBytes) throw new InvalidDataException("Missing or oversized capture: " + path);
                var manifest = JsonUtility.FromJson<TransferCaseCapture.Manifest>(File.ReadAllText(file.FullName));
                if (manifest == null || (manifest.schema != 1 && manifest.schema != 2) || manifest.pairs == null || manifest.pairs.Count > TransferCaseCapture.MaxPairs)
                    throw new InvalidDataException("Unsupported transfer capture: " + path);
                int added = 0;
                foreach (var pair in manifest.pairs) {
                    if (pair == null || pair.status != "complete") continue;
                    if (jobs.Count >= config.maxPairs) throw new InvalidDataException("Selected captures exceed maxPairs; select a smaller corpus or raise the limit.");
                    var captured = pair;
                    jobs.Add(new Job { name = Path.GetFileName(file.DirectoryName) + "/" + pair.index + "/" + pair.target,
                        load = () => LoadCapture(file, manifest.schema, captured) });
                    ++added;
                }
                if (added == 0) throw new InvalidDataException("Capture has no completed transfer pairs. Use Capture next run, then Transfer or Full Pipeline: " + path);
            }
            foreach (var assetCase in config.assetCases)
                foreach (var pair in TransferBenchmarkAssets.Pairs(assetCase)) {
                    if (jobs.Count >= config.maxPairs) throw new InvalidDataException("Imported assets exceed maxPairs.");
                    var assetPair = pair;
                    jobs.Add(new Job { name = pair.name, load = () => TransferBenchmarkAssets.Prepare(assetPair) });
                }
            if (jobs.Count == 0 || jobs.Count > config.maxPairs) throw new InvalidDataException("Invalid benchmark case count.");
            return jobs;
        }

        static Input LoadCapture(FileInfo file, int schema, TransferCaseCapture.Pair pair)
        {
            var input = new Input { name = Path.GetFileName(file.DirectoryName) + "/" + pair.index + "/" + pair.target,
                description = "Frozen capture; reference is the recorded output, not independent ground truth.", capture = file.FullName,
                sourceHash = pair.sourceMesh, targetHash = pair.targetMesh, atlasWidth = pair.atlasWidth, atlasHeight = pair.atlasHeight,
                localToWorld = pair.localToWorld, clamp = pair.clamp };
            try {
                var details = schema == 2 ? TransferCaseCapture.ReadDetails<TransferCaseCapture.PairDetails>(file.DirectoryName, pair.details) : null;
                input.overlapHints = schema == 2 ? details?.overlapHints : pair.overlapHints;
                input.matchHints = schema == 2 ? details?.matchHints : pair.matchHints;
                input.source = TransferCaseReplay.Load(file.DirectoryName, pair.sourceMesh);
                input.target = TransferCaseReplay.Load(file.DirectoryName, pair.targetMesh);
                var baseline = TransferCaseReplay.Load(file.DirectoryName, pair.outputMesh);
                try { input.reference = baseline.uv2; }
                finally { UnityEngine.Object.DestroyImmediate(baseline); }
                if (TransferMeshSnapshot.UvHash(input.reference) != pair.baselineUvHash) throw new InvalidDataException("Captured reference UV2 hash mismatch.");
                return input;
            }
            catch { input.Dispose(); throw; }
        }

        static async Task RunCase(Job job, Config config, Report report)
        {
            Input input = null;
            try { input = job.load(); ValidateInput(input); }
            catch (Exception error) {
                input?.Dispose();
                report.rows.Add(new Row { name = job.name, method = "input", error = error.ToString() });
                await Save(report); return;
            }
            using (input) {
                byte[] sourceBytes = TransferMeshSnapshot.Capture(input.source), targetBytes = TransferMeshSnapshot.Capture(input.target);
                input.sourceHash = TransferMeshSnapshot.Hash(sourceBytes); input.targetHash = TransferMeshSnapshot.Hash(targetBytes);
                await StoreMesh(report.folder, input.sourceHash, sourceBytes); await StoreMesh(report.folder, input.targetHash, targetBytes);
                await FreezeInput(input, report);
                var sourceQuality = Quality.From(TransferUvQuality.Measure(input.source, input.source.uv2, Vector2.one, Matrix4x4.identity));
                foreach (string method in config.methods) {
                    if (UvProgress.CancelRequested) break;
                    var row = MakeRow(input, method, sourceQuality); report.rows.Add(row);
                    try { await RunMethod(input, config, row, report); }
                    catch (OperationCanceledException) { report.cancelled = true; throw; }
                    catch (Exception error) { row.error = error.ToString(); }
                    await Save(report);
                }
            }
        }

        static Row MakeRow(Input input, string method, Quality sourceQuality) => new Row { name = input.name, method = method,
            description = input.description, capture = input.capture, sourceHash = input.sourceHash, targetHash = input.targetHash,
            referenceIsGroundTruth = input.referenceIsGroundTruth, negativeControl = input.negativeControl,
            vertices = input.target.vertexCount, sourceFaces = input.source.triangles.Length / 3, targetFaces = input.target.triangles.Length / 3,
            overlapHints = input.overlapHints?.Count ?? 0, matchHints = input.matchHints?.Count ?? 0, sourceQuality = sourceQuality };

        static async Task RunMethod(Input input, Config config, Row row, Report report)
        {
            for (int run = 0; run < config.warmup; ++run) {
                if (UvProgress.CancelRequested) throw new OperationCanceledException();
                await TransferBenchmarkMethods.Run(row.method, input);
            }
            var times = new double[config.repetitions]; TransferBenchmarkMethods.Output first = null;
            for (int run = 0; run < times.Length; ++run) {
                if (UvProgress.CancelRequested) throw new OperationCanceledException();
                var watch = Stopwatch.StartNew();
                var output = await TransferBenchmarkMethods.Run(row.method, input);
                watch.Stop(); times[run] = watch.Elapsed.TotalMilliseconds;
                CheckOutput(output, input.target.vertexCount);
                string hash = TransferMeshSnapshot.UvHash(output.uv), mapping = MappingHash(output);
                if (first == null) { first = output; row.uvHash = hash; row.mappingHash = mapping; }
                else row.deterministic &= hash == row.uvHash && mapping == row.mappingHash;
            }
            row.milliseconds = times; var sorted = (double[])times.Clone(); Array.Sort(sorted);
            row.minimumMilliseconds = sorted[0]; row.maximumMilliseconds = sorted[sorted.Length - 1];
            row.medianMilliseconds = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) * .5;
            row.misses = first.misses; row.fallbackVertices = first.fallbackVertices;
            var uv = (Vector2[])first.uv.Clone();
            if (input.clamp) row.clampedVertices = XatlasRepack.ClampUvsToUnit(uv);
            var qualityWatch = Stopwatch.StartNew();
            var quality = TransferUvQuality.Measure(input.target, uv, Vector2.one, input.localToWorld);
            qualityWatch.Stop(); row.qualityMilliseconds = qualityWatch.Elapsed.TotalMilliseconds; row.quality = Quality.From(quality);
            ReferenceError(input, uv, row);
            if (first.result != null) {
                row.matchedShells = first.result.shellsMatched; row.unmatchedShells = first.result.shellsUnmatched; row.rejectedShells = first.result.shellsRejected;
                row.interpolationShells = first.result.shellsInterpolation; row.transformShells = first.result.shellsTransform; row.mergedShells = first.result.shellsMerged;
            }
            // Capture diagnostics separately; tracing and quality scans do not inflate the timed runs.
            var trace = new TransferMatchTrace();
            var diagnostic = await TransferBenchmarkMethods.Run(row.method, input, trace);
            row.deterministic &= TransferMeshSnapshot.UvHash(diagnostic.uv) == row.uvHash && MappingHash(diagnostic) == row.mappingHash;
            row.inputUnchanged = input.sourceHash == TransferMeshSnapshot.Hash(TransferMeshSnapshot.Capture(input.source))
                && input.targetHash == TransferMeshSnapshot.Hash(TransferMeshSnapshot.Capture(input.target));
            row.details = await TransferCaseCapture.StoreDetailsAsync(report.folder, new Details { uv = uv, reference = input.reference,
                localToWorld = input.localToWorld, atlasWidth = input.atlasWidth, atlasHeight = input.atlasHeight,
                quality = quality, trace = trace, result = diagnostic.result });
            row.view = "view_" + (report.rows.Count - 1).ToString("D4", CultureInfo.InvariantCulture) + ".svg";
            await File.WriteAllTextAsync(Path.Combine(report.folder, row.view), TransferBenchmarkReport.View(input, row, uv));
        }

        [Serializable] sealed class Mapping
        {
            public int misses, fallbackVertices;
            public int[] shells, vertices, faces, methods, issues;
            public GroupedShellTransfer.ShellStatus[] status;
        }
        static string MappingHash(TransferBenchmarkMethods.Output output)
        {
            var r = output.result;
            return TransferMeshSnapshot.Hash(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Mapping { misses = output.misses,
                fallbackVertices = output.fallbackVertices, shells = r?.targetShellToSourceShell, vertices = r?.vertexToSourceShell,
                faces = r?.faceToTargetShell, methods = r?.targetShellMethod, issues = r?.targetShellIssues, status = r?.targetShellStatus })));
        }

        static void ReferenceError(Input input, Vector2[] uv, Row row)
        {
            double sum = 0, maximum = 0;
            for (int i = 0; i < uv.Length; ++i) {
                var delta = input.reference[i] - uv[i];
                double square = (double)delta.x * delta.x * Math.Max(1, input.atlasWidth) * Math.Max(1, input.atlasWidth)
                    + (double)delta.y * delta.y * Math.Max(1, input.atlasHeight) * Math.Max(1, input.atlasHeight);
                sum += square; maximum = Math.Max(maximum, square);
            }
            row.rmsReferenceTexels = Math.Sqrt(sum / Math.Max(1, uv.Length)); row.maximumReferenceTexels = Math.Sqrt(maximum);
            row.referencePass = row.maximumReferenceTexels <= .25;
        }

        static void ValidateInput(Input input)
        {
            if (input == null || !input.source || !input.target || input.source.vertexCount == 0 || input.target.vertexCount == 0)
                throw new InvalidDataException("Empty transfer input.");
            ValidateMesh(input.source, true); ValidateMesh(input.target, false);
            if (input.reference == null || input.reference.Length != input.target.vertexCount) throw new InvalidDataException("Invalid reference UV2 length.");
            CheckUv(input.reference);
            for (int i = 0; i < 16; ++i) if (!Finite(input.localToWorld[i])) throw new InvalidDataException("Non-finite world matrix.");
        }
        static void ValidateMesh(Mesh mesh, bool source)
        {
            if (!mesh.isReadable) throw new InvalidDataException("Benchmark meshes must be readable snapshots.");
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) throw new InvalidDataException("Transfer benchmark requires triangle topology.");
            if (mesh.triangles.Length == 0) throw new InvalidDataException("Input has no triangles.");
            foreach (var vertex in mesh.vertices) if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z)) throw new InvalidDataException("Non-finite position.");
            if (mesh.uv.Length != mesh.vertexCount || (source && mesh.uv2.Length != mesh.vertexCount)) throw new InvalidDataException("Transfer requires complete UV0 and source UV2.");
            CheckUv(mesh.uv); if (source) CheckUv(mesh.uv2);
        }
        static void CheckOutput(TransferBenchmarkMethods.Output output, int count)
        {
            if (output == null || output.uv == null || output.uv.Length != count) throw new InvalidDataException("Transfer produced incomplete UV2.");
            CheckUv(output.uv);
        }
        static void CheckUv(Vector2[] uv)
        { foreach (var value in uv) if (!Finite(value.x) || !Finite(value.y)) throw new InvalidDataException("Non-finite UV."); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static async Task StoreMesh(string folder, string hash, byte[] bytes)
        {
            string directory = Path.Combine(folder, "meshes"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, hash + ".bin");
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        }

        static async Task FreezeInput(Input input, Report report)
        {
            var baseline = UnityEngine.Object.Instantiate(input.target);
            string baselineHash;
            try {
                baseline.uv2 = input.reference;
                var bytes = TransferMeshSnapshot.Capture(baseline); baselineHash = TransferMeshSnapshot.Hash(bytes);
                await StoreMesh(report.folder, baselineHash, bytes);
            }
            finally { UnityEngine.Object.DestroyImmediate(baseline); }
            string details = await TransferCaseCapture.StoreDetailsAsync(report.folder, new TransferCaseCapture.PairDetails {
                overlapHints = input.overlapHints, matchHints = input.matchHints });
            report.corpus.pairs.Add(new TransferCaseCapture.Pair { index = report.corpus.pairs.Count, source = input.source.name, target = input.name,
                sourceMesh = input.sourceHash, targetMesh = input.targetHash, outputMesh = baselineHash, baselineUvHash = TransferMeshSnapshot.UvHash(input.reference),
                atlasWidth = input.atlasWidth, atlasHeight = input.atlasHeight, localToWorld = input.localToWorld, clamp = input.clamp, details = details, status = "complete" });
        }

        internal static async Task Save(Report report)
        {
            if (report.rows.Count > MaxRows) throw new InvalidDataException("Benchmark report exceeds the row limit.");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(report, true));
            if (bytes.LongLength > TransferCaseCapture.MaxManifestBytes) throw new InvalidDataException("Benchmark metadata exceeds the size limit.");
            string path = Path.Combine(report.folder, "comparison.json"), temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            if (report.corpus != null) await File.WriteAllTextAsync(Path.Combine(report.folder, "manifest.json"), JsonUtility.ToJson(report.corpus, true));
            await File.WriteAllTextAsync(Path.Combine(report.folder, "comparison.csv"), TransferBenchmarkReport.Csv(report));
            await File.WriteAllTextAsync(Path.Combine(report.folder, "index.html"), TransferBenchmarkReport.Html(report));
        }
    }
}
