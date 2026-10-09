using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One geometry-based score for projection candidates and the final shell.</summary>
    [Serializable]
    internal sealed class TransferCandidateQuality
    {
        public int issues, invalidFaces, stretchedFaces, overlapPairs, outOfBoundsVertices;
        public double worstAnisotropy, areaWeightedAnisotropy, density, densityVariation, overlapArea;
        public bool overlapScanComplete;

        internal static TransferCandidateQuality Measure(UvShell shell, int[] triangles, Vector3[] positions,
            Dictionary<int, Vector2> uv, bool scanOverlap = true)
        {
            var quality = new TransferCandidateQuality {
                issues = GroupedShellTransfer.CountShellIssues(shell.faceIndices, triangles, positions, uv)
            };
            double weighted = 0, totalArea = 0, densitySum = 0, densitySquare = 0;
            foreach (int face in shell.faceIndices) {
                int a = triangles[face * 3], b = triangles[face * 3 + 1], c = triangles[face * 3 + 2];
                if (!uv.TryGetValue(a, out var u) || !uv.TryGetValue(b, out var v) || !uv.TryGetValue(c, out var w)
                    || !Finite(u) || !Finite(v) || !Finite(w)) { ++quality.invalidFaces; continue; }
                double area = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude;
                if (!(area > 0)) continue;
                double stretch = GroupedShellTransfer.TriangleStretch(positions[a], positions[b], positions[c], u, v, w);
                // A line contributes a large finite penalty; JSON cannot represent infinity.
                if (double.IsNaN(stretch) || double.IsInfinity(stretch)) stretch = 1e12;
                quality.worstAnisotropy = Math.Max(quality.worstAnisotropy, stretch);
                if (stretch > 4) ++quality.stretchedFaces;
                weighted += stretch * area; totalArea += area;
                double uvArea = Math.Abs(((double)v.x - u.x) * ((double)w.y - u.y)
                    - ((double)w.x - u.x) * ((double)v.y - u.y));
                double faceDensity = Math.Sqrt(uvArea / area);
                densitySum += faceDensity * area; densitySquare += faceDensity * faceDensity * area;
            }
            if (totalArea > 0) {
                quality.areaWeightedAnisotropy = weighted / totalArea;
                quality.density = densitySum / totalArea;
                quality.densityVariation = quality.density > 0
                    ? Math.Sqrt(Math.Max(0, densitySquare / totalArea - quality.density * quality.density)) / quality.density : 0;
            }
            foreach (var pair in uv) {
                if (!Finite(pair.Value) || pair.Value.x < 0 || pair.Value.x > 1 || pair.Value.y < 0 || pair.Value.y > 1)
                    ++quality.outOfBoundsVertices;
            }
            if (scanOverlap) quality.ScanOverlap(shell, triangles, positions.Length, uv);
            return quality;
        }

        void ScanOverlap(UvShell shell, int[] triangles, int vertexCount, Dictionary<int, Vector2> candidate)
        {
            var uv = new Vector2[vertexCount];
            foreach (var point in candidate) uv[point.Key] = point.Value;
            var indices = new int[shell.faceIndices.Count * 3]; int index = 0;
            foreach (int face in shell.faceIndices)
                for (int corner = 0; corner < 3; ++corner) indices[index++] = triangles[face * 3 + corner];
            var atlas = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {
                uv = uv, indices = indices, charts = new int[vertexCount]
            }, CancellationToken.None, comparisonBudget: 200000);
            overlapPairs = atlas.pairs; overlapArea = atlas.pairAreaSum; overlapScanComplete = atlas.complete;
        }

        internal bool Improves(TransferCandidateQuality other)
        {
            if (invalidFaces > other.invalidFaces || issues > other.issues || outOfBoundsVertices > other.outOfBoundsVertices
                || !overlapScanComplete || overlapPairs > other.overlapPairs
                || overlapArea > other.overlapArea * 1.001 + 1e-14) return false;
            if (worstAnisotropy > other.worstAnisotropy * 1.01
                || areaWeightedAnisotropy > other.areaWeightedAnisotropy * 1.01) return false;
            // A floating-point sliver is not a repair of a line. Require fewer
            // collapsed faces before accepting a still effectively collapsed map.
            if (worstAnisotropy > 10000 && issues >= other.issues) return false;
            return issues < other.issues || overlapPairs < other.overlapPairs
                || worstAnisotropy < other.worstAnisotropy * .99
                || areaWeightedAnisotropy < other.areaWeightedAnisotropy * .99;
        }

        internal bool NeedsRecovery => invalidFaces > 0 || issues > 0 || worstAnisotropy > 4;
        static bool Finite(Vector2 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
    }
}
