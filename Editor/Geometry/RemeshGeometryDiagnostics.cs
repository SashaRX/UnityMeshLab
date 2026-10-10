using System;
using System.IO;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Exact inputs and stage geometry for reproducing upstream holes.</summary>
    internal static class RemeshGeometryDiagnostics
    {
        static readonly object FailureWriteLock = new object();
        internal const int MaxFailureCaptures = 32;
        internal const long MaxFailureCaptureBytes = 512L * 1024 * 1024;

        [Serializable]
        internal sealed class FailureMetadata
        {
            public string stage, node, reason, settingsJson;
            public int resolution;
            public int capRevision;
            public int[] sourceFaceOwners;
            public int externalContactCount;
            public int[] sourceFaceElements, preparedFaceElements;
            public long excludedElementPairs;
            public int[] refusedLoops;
            public string[] refusedLoopReasons;
            public string closureSelectionWarning;
            public int firstContactAddedFace = -1, firstContactSourceFace = -1;
            public uint initialFlags, resultFlags;
        }

        // Failure captures do not depend on Verbose/category settings. Otherwise a
        // rejected raw voxel never reaches Capture and its reproducible input is lost.
        internal static string CaptureFailure(Vector3[] sourcePositions, int[] sourceIndices,
            RemeshNative.IndexedMesh raw, RemeshNative.IndexedMesh input, RemeshSettings settings,
            string stage, string node, string reason, int resolution, uint initialFlags, uint resultFlags)
            => CaptureFailure(sourcePositions, sourceIndices, raw, input, settings, stage, node, reason, resolution, initialFlags, resultFlags, null);

        internal static string CaptureFailure(Vector3[] sourcePositions, int[] sourceIndices,
            RemeshNative.IndexedMesh raw, RemeshNative.IndexedMesh input, RemeshSettings settings,
            string stage, string node, string reason, int resolution, uint initialFlags, uint resultFlags, RemeshPlanarCap.Support support,
            int[] sourceFaceOwners = null)
        {
            try {
                string path = WriteFailure(Path.Combine(Path.GetTempPath(), "meshlab-uvmerge", "failures"),
                    sourcePositions, sourceIndices, raw, input, new FailureMetadata {
                        stage = stage, node = node, reason = reason, settingsJson = JsonUtility.ToJson(settings),
                        resolution = resolution, initialFlags = initialFlags, resultFlags = resultFlags,
                        capRevision = support == null ? 0 : RemeshPlanarCap.Revision,
                        sourceFaceOwners = sourceFaceOwners ?? support?.externalContacts?.faceOwners,
                        externalContactCount = support?.externalContacts?.count ?? 0,
                        firstContactAddedFace = support?.externalContacts?.firstNewFace ?? -1,
                        firstContactSourceFace = support?.externalContacts?.firstSourceFace ?? -1
                    }, support);
                UvtLog.Warn("[Remesh] " + stage + " failure geometry and settings captured to " + path);
                return path;
            }
            catch (Exception error) {
                UvtLog.Warn("[Remesh] Failure geometry capture could not be written: " + error.Message);
                return null; // Diagnostic I/O must never replace the topology exception.
            }
        }

        internal static string WriteFailure(string directory, Vector3[] sourcePositions, int[] sourceIndices,
            RemeshNative.IndexedMesh raw, RemeshNative.IndexedMesh input, FailureMetadata metadata)
            => WriteFailure(directory, sourcePositions, sourceIndices, raw, input, metadata, null);

        internal static string WriteFailure(string directory, Vector3[] sourcePositions, int[] sourceIndices,
            RemeshNative.IndexedMesh raw, RemeshNative.IndexedMesh input, FailureMetadata metadata, RemeshPlanarCap.Support support)
        {
            lock (FailureWriteLock) {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "remesh_failure_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") +
                    "_" + Guid.NewGuid().ToString("N") + ".bin");
                string partial = path + ".tmp";
                try {
                    if (support != null) {
                        metadata.sourceFaceElements = support.externalContacts?.faceElements;
                        metadata.preparedFaceElements = support.faceElements;
                        metadata.excludedElementPairs = support.externalContacts?.excludedElementPairs ?? 0;
                        metadata.closureSelectionWarning = support.selectionWarning;
                        metadata.refusedLoops = new int[support.loopFailures.Count];
                        metadata.refusedLoopReasons = new string[support.loopFailures.Count];
                        int entry = 0;
                        foreach (var failure in support.loopFailures) {
                            metadata.refusedLoops[entry] = failure.Key; metadata.refusedLoopReasons[entry++] = failure.Value;
                        }
                    }
                    using (var writer = new BinaryWriter(File.Create(partial))) {
                        writer.Write(0x524D4C42); writer.Write(support == null ? 2 : 3);
                        writer.Write(JsonUtility.ToJson(metadata));
                        WriteOptional(writer, sourcePositions, sourceIndices);
                        if (support != null) WriteOptional(writer, support.positions, support.indices);
                        WriteOptional(writer, raw?.positions, raw?.indices);
                        WriteOptional(writer, input?.positions, input?.indices);
                    }
                    File.Move(partial, path);
                }
                finally {
                    if (File.Exists(partial)) File.Delete(partial);
                }
                PruneFailureCaptures(directory, path);
                return path;
            }
        }

        internal static void PruneFailureCaptures(string directory, string latest,
            int maxFiles = MaxFailureCaptures, long maxBytes = MaxFailureCaptureBytes)
        {
            if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            // Equal timestamps must not prune the capture that was just returned.
            var stale = Array.FindAll(Directory.GetFiles(directory, "remesh_failure_*.bin"),
                item => !string.Equals(item, latest, StringComparison.Ordinal));
            Array.Sort(stale, StringComparer.Ordinal);
            long bytes = new FileInfo(latest).Length;
            foreach (string path in stale) bytes += new FileInfo(path).Length;
            int count = stale.Length + 1;
            foreach (string path in stale) {
                if (count <= maxFiles && bytes <= maxBytes) break;
                long length = new FileInfo(path).Length;
                File.Delete(path); --count; bytes -= length;
            }
            // One oversized latest capture stays available for diagnosis.
        }

        static void WriteOptional(BinaryWriter writer, Vector3[] positions, int[] indices)
        {
            bool present = positions != null && indices != null;
            writer.Write(present);
            if (present) Write(writer, positions, indices);
        }

        internal static string CaptureSupport(RemeshSource source, RemeshPlanarCap.Support support, RemeshSettings settings, string node,
            string directory = null)
        {
            // Refused contours and invalid selections remain reproducible at every
            // log level. Ordinary successful preparation only dumps at Verbose.
            if (support.loopFailures.Count == 0 && support.selectionWarning == null &&
                (UvtLog.Current < UvtLog.Level.Verbose || !UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag))) return null;
            try {
                string path = WriteFailure(directory ?? Path.Combine(Path.GetTempPath(), "meshlab-uvmerge", "cap"), source.positions, source.indices,
                    null, null, new FailureMetadata { stage = "Cap preparation", node = node, reason = support.Description,
                        settingsJson = JsonUtility.ToJson(settings), capRevision = RemeshPlanarCap.Revision,
                        sourceFaceOwners = source.FaceOwners(), externalContactCount = support.externalContacts?.count ?? 0,
                        firstContactAddedFace = support.externalContacts?.firstNewFace ?? -1,
                        firstContactSourceFace = support.externalContacts?.firstSourceFace ?? -1 }, support);
                UvtLog.Info("[Remesh] Original donor and prepared Cap support captured to " + path);
                return path;
            }
            catch (Exception error) { UvtLog.Warn("[Remesh] Cap support capture failed: " + error.Message); return null; }
        }

        internal static void Capture(RemeshSource source, RemeshNative.IndexedMesh raw,
            RemeshNative.IndexedMesh trimmed, RemeshSettings settings)
        {
            if (UvtLog.Current < UvtLog.Level.Info || !UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) return;
            try {
                // The dump is the source, the raw voxel and the trimmed mesh in full, written
                // on every remesh: Verbose only. The topology summary stays an Info line.
                if (UvtLog.Current >= UvtLog.Level.Verbose) {
                    string dir = Path.Combine(Path.GetTempPath(), "meshlab-uvmerge");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "remesh_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
                        "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bin");
                    using (var writer = new BinaryWriter(File.Create(path))) {
                        writer.Write(0x524D4C42); writer.Write(1);
                        writer.Write(JsonUtility.ToJson(settings));
                        Write(writer, source.positions, source.indices);
                        Write(writer, raw.positions, raw.indices);
                        Write(writer, trimmed.positions, trimmed.indices);
                    }
                    UvtLog.Verbose(UvtLog.Category.RemeshDiag, "Source, raw voxel, trimmed geometry and settings captured to " + path);
                    var stale = Directory.GetFiles(dir, "remesh_*.bin");
                    Array.Sort(stale, StringComparer.Ordinal);
                    for (int i = 0; i < stale.Length - 3; i++) File.Delete(stale[i]);
                }
                UvtLog.Info(UvtLog.Category.RemeshDiag, "Remesh stage topology: source [" +
                    RemeshTopology.Inspect(source.positions, source.indices).Description + "]; raw voxel [" +
                    RemeshTopology.Inspect(raw.positions, raw.indices).Description + "]; trimmed [" +
                    RemeshTopology.Inspect(trimmed.positions, trimmed.indices).Description + "].");
            }
            catch (Exception error) { UvtLog.Warn(UvtLog.Category.RemeshDiag, "Remesh input capture failed: " + error.Message); }
        }

        static void Write(BinaryWriter writer, Vector3[] positions, int[] indices)
        {
            writer.Write(positions.Length); writer.Write(indices.Length);
            foreach (var p in positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (int index in indices) writer.Write(index);
        }
    }
}
