// FbxPolygonChecks.cs — checks of the FBX's own polygons, which Unity's import triangulates
// before any mesh check can see them: a concave polygon (four corners or more) triangulates
// unevenly, and how depends on the importer. Reported per mesh into a PrepareReport.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using Autodesk.Fbx;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxPolygonChecks
    {
        /// <summary>Reports the concave polygons of each mesh of the FBX at <paramref name="fbxPath"/>.</summary>
        internal static void CheckConcave(string fbxPath, ModelRequirements requirements, PrepareReport report)
        {
            if (requirements.concaveFaces == RequirementLevel.Off || !fbxPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            Dictionary<string, int> concave;
            try { concave = ConcavePolygons(AssetFilePath.Physical(fbxPath)); }
            catch (Exception ex)
            {
                // The check did not run: say so, as the smoothing check does for a file it cannot read.
                report.Add(PrepareLevel.Warning, fbxPath, "Concave faces", $"the file's polygons could not be read: {ex.Message}");
                return;
            }
            foreach (var mesh in concave)
                report.Add(requirements.concaveFaces, $"'{mesh.Key}'", "Concave faces",
                    $"{MeshValidation.Plural(mesh.Value, "concave polygon", "concave polygons")} in the FBX; they triangulate unevenly");
        }

        /// <summary>The number of concave polygons of each mesh of the FBX at <paramref name="path"/> (by its node's name), meshes with none left out.</summary>
        internal static Dictionary<string, int> ConcavePolygons(string path)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            using var document = FbxSourceDocument.Load(path);
            foreach (var mesh in document.Meshes)
            {
                int count = Concave(mesh);
                if (count == 0) continue;
                string name = mesh.GetNodeCount() > 0 ? mesh.GetNode(0).GetName() : mesh.GetName();
                result.TryGetValue(name, out int sum);
                result[name] = sum + count;
            }
            return result;
        }

        static int Concave(FbxMesh mesh)
        {
            var points = new Vector3[mesh.GetControlPointsCount()];
            for (int i = 0; i < points.Length; i++)
            {
                var p = mesh.GetControlPointAt(i);
                points[i] = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            }
            int count = 0;
            var corners = new List<Vector3>();
            for (int polygon = 0; polygon < mesh.GetPolygonCount(); polygon++)
            {
                int size = mesh.GetPolygonSize(polygon);
                if (size < 4) continue;
                corners.Clear();
                for (int k = 0; k < size; k++)
                {
                    int index = mesh.GetPolygonVertex(polygon, k);
                    if (index >= 0 && index < points.Length) corners.Add(points[index]);
                }
                if (MeshValidation.IsConcave(corners)) count++;
            }
            return count;
        }
    }
}
#endif
