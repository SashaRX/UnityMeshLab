// FbxPolygonChecks.cs — checks of the FBX's own polygons, which Unity's import triangulates
// before any mesh check can see them: a concave polygon (four corners or more) triangulates
// unevenly, and how depends on the importer. Reported per mesh into a PrepareReport. Also the
// count of a file's quads and n-gons, for a save to say which of them it turned into triangles.

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

        /// <summary>
        /// The quads and the n-gons (five corners or more) of each mesh of the FBX at
        /// <paramref name="path"/>, by its node's name (meshes of one name summed); a mesh of
        /// triangles alone is listed with none.
        /// </summary>
        internal static Dictionary<string, (int quads, int ngons)> LargePolygons(string path)
        {
            var result = new Dictionary<string, (int quads, int ngons)>(StringComparer.Ordinal);
            using var document = FbxSourceDocument.Load(path);
            foreach (var mesh in document.Meshes)
            {
                string name = mesh.GetNodeCount() > 0 ? mesh.GetNode(0).GetName() : mesh.GetName();
                var (q, n) = LargePolygons(mesh);
                result.TryGetValue(name, out var sum);
                result[name] = (sum.quads + q, sum.ngons + n);
            }
            return result;
        }

        /// <summary>
        /// The polygons a re-export turned into triangles, in words, from each mesh's quads and
        /// n-gons before and after it; null when none were. Only a mesh found under its name in
        /// both counts: one that is gone or new was removed or added, not triangulated, and
        /// quads added to one mesh do not hide those another lost.
        /// </summary>
        internal static string Triangulated(IReadOnlyDictionary<string, (int quads, int ngons)> before,
            IReadOnlyDictionary<string, (int quads, int ngons)> after)
        {
            int quads = 0, ngons = 0;
            foreach (var mesh in before)
            {
                if (!after.TryGetValue(mesh.Key, out var now)) continue;
                quads += Math.Max(0, mesh.Value.quads - now.quads);
                ngons += Math.Max(0, mesh.Value.ngons - now.ngons);
            }
            return Triangulated(quads, ngons, 0, 0);
        }

        /// <summary>The quads and the n-gons (five corners or more) of <paramref name="mesh"/>.</summary>
        internal static (int quads, int ngons) LargePolygons(FbxMesh mesh)
        {
            int quads = 0, ngons = 0;
            for (int polygon = 0; polygon < mesh.GetPolygonCount(); polygon++)
            {
                int size = mesh.GetPolygonSize(polygon);
                if (size == 4) quads++;
                else if (size > 4) ngons++;
            }
            return (quads, ngons);
        }

        /// <summary>
        /// The polygons a save turned into triangles, in words ("12 quads and 3 n-gons"), from
        /// the quads and n-gons before and after it; null when none were.
        /// </summary>
        internal static string Triangulated(int quadsBefore, int ngonsBefore, int quadsAfter, int ngonsAfter)
        {
            int quads = Math.Max(0, quadsBefore - quadsAfter), ngons = Math.Max(0, ngonsBefore - ngonsAfter);
            var parts = new List<string>();
            if (quads > 0) parts.Add(MeshValidation.Plural(quads, "quad", "quads"));
            if (ngons > 0) parts.Add(MeshValidation.Plural(ngons, "n-gon", "n-gons"));
            return parts.Count == 0 ? null : string.Join(" and ", parts);
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
