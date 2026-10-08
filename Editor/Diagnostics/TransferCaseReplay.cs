using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TransferCaseReplay
    {
        [Serializable] internal sealed class PairReport
        {
            public int index, changedVertices, changedMappings;
            public string target, baselineHash, replayHash, repeatHash, error, details;
            public bool baselineEqual, repeatEqual;
            public float maximumUvDelta;
        }
        [Serializable] internal sealed class PairDetails
        {
            public TransferUvQuality quality;
            public TransferMatchTrace trace;
            public GroupedShellTransfer.TransferResult result;
        }
        [Serializable] internal sealed class Report
        {
            public string capture, capturedStatus, unityVersion, folder, error;
            public bool complete;
            public List<PairReport> pairs = new List<PairReport>();
        }

        internal static async Task<Report> Replay(string manifestPath)
        {
            if (UvProgress.IsActive) throw new InvalidOperationException("Wait for the active operation to finish.");
            var file = new FileInfo(manifestPath);
            if (!file.Exists || file.Length > TransferCaseCapture.MaxManifestBytes) throw new InvalidDataException("Missing or oversized transfer manifest.");
            string json = await File.ReadAllTextAsync(file.FullName);
            if (UvProgress.IsActive) throw new InvalidOperationException("Wait for the active operation to finish.");
            var manifest = JsonUtility.FromJson<TransferCaseCapture.Manifest>(json);
            if (manifest == null || (manifest.schema != 1 && manifest.schema != 2) || manifest.pairs == null || manifest.pairs.Count > TransferCaseCapture.MaxPairs)
                throw new InvalidDataException("Unsupported transfer manifest.");
            string root = file.DirectoryName;
            int ready = 0;
            foreach (var pair in manifest.pairs) {
                if (pair.status != "complete") continue;
                Verify(root, pair.sourceMesh); Verify(root, pair.targetMesh); Verify(root, pair.outputMesh);
                if (manifest.schema == 2) Verify(root, pair.details, "details", ".json");
                ++ready;
            }
            if (ready == 0) throw new InvalidDataException("This capture has no completed transfer pairs. Arm Capture next run, then run Transfer or Full Pipeline.");
            var report = new Report { capture = file.FullName, capturedStatus = manifest.status, unityVersion = Application.unityVersion,
                folder = Path.Combine(root, "replay_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                    + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)) };
            Directory.CreateDirectory(report.folder);
            UvProgress.Begin("Replay transfer capture", cancelable: true);
            try {
                foreach (var pair in manifest.pairs) {
                    if (pair.status != "complete") continue;
                    if (UvProgress.CancelRequested) break;
                    UvProgress.Report((float)report.pairs.Count / ready, pair.target);
                    if (manifest.schema == 2) TransferCaseCapture.RestorePairDetails(root, pair);
                    var item = await ReplayPair(root, pair, report.folder);
                    report.pairs.Add(item);
                    // The next pair does not need the previous capture's large diagnostic arrays.
                    pair.result = null; pair.trace = null; pair.quality = null; pair.validation = null;
                    pair.overlapHints = null; pair.matchHints = null;
                }
                report.complete = report.pairs.Count == ready && !UvProgress.CancelRequested && report.pairs.TrueForAll(pair => string.IsNullOrEmpty(pair.error));
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally {
                try { await SaveReport(report); }
                finally { if (UvProgress.CancelRequested) UvProgress.Cancel(); else UvProgress.End(); }
            }
            UvtLog.Info(UvtLog.Category.Benchmark, "[TransferReplay] " + Path.Combine(report.folder, "replay.json"));
            return report;
        }

        internal static async Task SaveReport(Report report)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(report, true));
            if (bytes.LongLength > TransferCaseCapture.MaxManifestBytes) throw new InvalidDataException("Transfer replay report exceeds the size limit.");
            await File.WriteAllBytesAsync(Path.Combine(report.folder, "replay.json"), bytes);
        }

        static async Task<PairReport> ReplayPair(string root, TransferCaseCapture.Pair pair, string folder)
        {
            var item = new PairReport { index = pair.index, target = pair.target, baselineHash = pair.baselineUvHash };
            var details = new PairDetails { trace = new TransferMatchTrace() };
            Mesh source = null, target = null, baseline = null;
            try {
                source = Load(root, pair.sourceMesh); target = Load(root, pair.targetMesh); baseline = Load(root, pair.outputMesh);
                ValidateInput(source, true); ValidateInput(target, false);
                var first = await Run(pair, target, source, details.trace);
                if (first.uv2 == null) throw new InvalidDataException("Replay produced no UV2.");
                details.result = first;
                item.replayHash = TransferMeshSnapshot.UvHash(first.uv2);
                item.baselineEqual = item.replayHash == pair.baselineUvHash;
                var expected = baseline.uv2;
                if (TransferMeshSnapshot.UvHash(expected) != pair.baselineUvHash) throw new InvalidDataException("Baseline UV2 hash disagrees with the captured output mesh.");
                if (expected.Length != first.uv2.Length) item.changedVertices = Math.Max(expected.Length, first.uv2.Length);
                else for (int i = 0; i < expected.Length; ++i) {
                    var delta = expected[i] - first.uv2[i];
                    if (!SameUvBits(expected[i], first.uv2[i])) ++item.changedVertices;
                    item.maximumUvDelta = Mathf.Max(item.maximumUvDelta, delta.magnitude);
                }
                item.changedMappings = MappingChanges(pair.result, first);
                details.quality = TransferUvQuality.Measure(target, first.uv2, Vector2.one, pair.localToWorld);
                var repeat = await Run(pair, target, source, null);
                item.repeatHash = TransferMeshSnapshot.UvHash(repeat.uv2);
                item.repeatEqual = item.repeatHash == item.replayHash && MappingChanges(first, repeat) == 0;
            }
            catch (Exception error) { item.error = error.ToString(); }
            finally {
                if (source) UnityEngine.Object.DestroyImmediate(source);
                if (target) UnityEngine.Object.DestroyImmediate(target);
                if (baseline) UnityEngine.Object.DestroyImmediate(baseline);
            }
            // Only this pair's arrays remain alive while writing; report.pairs keeps metadata.
            try { item.details = await TransferCaseCapture.StoreDetailsAsync(folder, details); }
            catch (Exception error) { item.error = (item.error ?? "") + error; }
            return item;
        }

        static async Task<GroupedShellTransfer.TransferResult> Run(TransferCaseCapture.Pair pair, Mesh target, Mesh source, TransferMatchTrace trace)
        {
            var result = await GroupedShellTransfer.TransferAsyncWithDiagnostics(target, source, pair.overlapHints, pair.matchHints,
                pair.atlasWidth, pair.atlasHeight, trace);
            if (pair.clamp && result.uv2 != null) XatlasRepack.ClampUvsToUnit(result.uv2);
            return result;
        }

        static int MappingChanges(GroupedShellTransfer.TransferResult a, GroupedShellTransfer.TransferResult b)
        {
            if (a == null || b == null) return -1;
            return Differences(a.targetShellToSourceShell, b.targetShellToSourceShell)
                + Differences(a.vertexToSourceShell, b.vertexToSourceShell) + Differences(a.faceToTargetShell, b.faceToTargetShell)
                + Differences(a.targetShellMethod, b.targetShellMethod) + Differences(a.targetShellStatus, b.targetShellStatus)
                + Differences(a.targetShellIssues, b.targetShellIssues);
        }

        static int Differences<T>(T[] a, T[] b)
        {
            if (a == null || b == null) return a == b ? 0 : 1;
            int changed = Math.Abs(a.Length - b.Length);
            for (int i = 0; i < Math.Min(a.Length, b.Length); ++i)
                if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) ++changed;
            return changed;
        }

        internal static Mesh Load(string root, string hash) => TransferMeshSnapshot.Restore(Verify(root, hash));
        internal static byte[] Verify(string root, string hash, string directory = "meshes", string extension = ".bin")
        {
            if (hash == null || hash.Length != 64) throw new InvalidDataException("Invalid mesh hash.");
            foreach (char character in hash)
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))
                    throw new InvalidDataException("Invalid mesh hash.");
            // Hash-only names cannot escape the selected capture directory.
            var file = new FileInfo(Path.Combine(root, directory, hash + extension));
            if (!file.Exists || file.Length > TransferMeshSnapshot.MaxFileBytes) throw new InvalidDataException("Missing or oversized mesh snapshot.");
            var bytes = File.ReadAllBytes(file.FullName);
            if (TransferMeshSnapshot.Hash(bytes) != hash) throw new InvalidDataException("Mesh snapshot checksum mismatch.");
            return bytes;
        }

        static void ValidateInput(Mesh mesh, bool source)
        {
            foreach (var vertex in mesh.vertices)
                if (!Finite(vertex.x) || !Finite(vertex.y) || !Finite(vertex.z)) throw new InvalidDataException("Non-finite mesh position.");
            var uv = mesh.uv;
            if (uv.Length != mesh.vertexCount) throw new InvalidDataException("Transfer requires full UV0.");
            CheckUv(uv);
            if (source) {
                uv = mesh.uv2;
                if (uv.Length != mesh.vertexCount) throw new InvalidDataException("Source requires full UV2.");
                CheckUv(uv);
            }
        }
        static void CheckUv(Vector2[] uv)
        { foreach (var value in uv) if (!Finite(value.x) || !Finite(value.y)) throw new InvalidDataException("Non-finite UV."); }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool SameUvBits(Vector2 a, Vector2 b)
            => BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x)
                && BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y);
    }
}
