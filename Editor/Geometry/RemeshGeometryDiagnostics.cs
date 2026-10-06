using System;
using System.IO;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Exact inputs and stage geometry for reproducing upstream holes.</summary>
    internal static class RemeshGeometryDiagnostics
    {
        internal static void Capture(RemeshSource source, RemeshNative.IndexedMesh raw,
            RemeshNative.IndexedMesh trimmed, RemeshSettings settings)
        {
            if (UvtLog.Current < UvtLog.Level.Info || !UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) return;
            try {
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
                UvtLog.Info(UvtLog.Category.RemeshDiag, "Source, raw voxel, trimmed geometry and settings captured to " + path);
                UvtLog.Info(UvtLog.Category.RemeshDiag, "Remesh stage topology: source [" +
                    RemeshTopology.Inspect(source.positions, source.indices).Description + "]; raw voxel [" +
                    RemeshTopology.Inspect(raw.positions, raw.indices).Description + "]; trimmed [" +
                    RemeshTopology.Inspect(trimmed.positions, trimmed.indices).Description + "].");
                var stale = Directory.GetFiles(dir, "remesh_*.bin");
                Array.Sort(stale, StringComparer.Ordinal);
                for (int i = 0; i < stale.Length - 3; i++) File.Delete(stale[i]);
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
