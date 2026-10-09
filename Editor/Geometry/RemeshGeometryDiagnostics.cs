using System;
using System.IO;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Exact inputs and stage geometry for reproducing upstream holes.</summary>
    internal static class RemeshGeometryDiagnostics
    {
        static readonly object FailureWriteLock = new object();

        [Serializable]
        internal sealed class FailureMetadata
        {
            public string stage, node, reason, settingsJson;
            public int resolution;
            public uint initialFlags, resultFlags;
        }

        // Failure captures do not depend on Verbose/category settings. Otherwise a
        // rejected raw voxel never reaches Capture and its reproducible input is lost.
        internal static string CaptureFailure(Vector3[] sourcePositions, int[] sourceIndices,
            RemeshNative.IndexedMesh raw, RemeshNative.IndexedMesh input, RemeshSettings settings,
            string stage, string node, string reason, int resolution, uint initialFlags, uint resultFlags)
        {
            try {
                string path = WriteFailure(Path.Combine(Path.GetTempPath(), "meshlab-uvmerge", "failures"),
                    sourcePositions, sourceIndices, raw, input, new FailureMetadata {
                        stage = stage, node = node, reason = reason, settingsJson = JsonUtility.ToJson(settings),
                        resolution = resolution, initialFlags = initialFlags, resultFlags = resultFlags
                    });
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
        {
            lock (FailureWriteLock) {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "remesh_failure_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") +
                    "_" + Guid.NewGuid().ToString("N") + ".bin");
                string partial = path + ".tmp";
                try {
                    using (var writer = new BinaryWriter(File.Create(partial))) {
                        writer.Write(0x524D4C42); writer.Write(2);
                        writer.Write(JsonUtility.ToJson(metadata));
                        WriteOptional(writer, sourcePositions, sourceIndices);
                        WriteOptional(writer, raw?.positions, raw?.indices);
                        WriteOptional(writer, input?.positions, input?.indices);
                    }
                    File.Move(partial, path);
                }
                finally {
                    if (File.Exists(partial)) File.Delete(partial);
                }
                // Equal timestamps must not prune the capture that was just returned.
                var stale = Array.FindAll(Directory.GetFiles(directory, "remesh_failure_*.bin"),
                    item => !string.Equals(item, path, StringComparison.Ordinal));
                Array.Sort(stale, StringComparer.Ordinal);
                for (int i = 0; i < stale.Length - 2; i++) File.Delete(stale[i]);
                return path;
            }
        }

        static void WriteOptional(BinaryWriter writer, Vector3[] positions, int[] indices)
        {
            bool present = positions != null && indices != null;
            writer.Write(present);
            if (present) Write(writer, positions, indices);
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
