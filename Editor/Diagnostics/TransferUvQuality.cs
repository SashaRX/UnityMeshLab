using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Axis stretch is measured against geometry, independently of UV area/density.</summary>
    [Serializable]
    internal sealed class TransferUvQuality
    {
        public int faces, degenerateFaces, invalidFaces, stretchedFaces;
        public double worstAnisotropy, areaWeightedAnisotropy;
        public float[] faceAnisotropy;
        public int overlapPairs, outOfBoundsVertices;
        public bool overlapScanComplete;
        public long overlapComparisons;
        public double overlapPairArea;
        public List<string> overlapSamples = new List<string>();

        internal static TransferUvQuality Measure(Mesh mesh, Vector2[] uv, Vector2 metric, Matrix4x4 localToWorld)
        {
            var report = new TransferUvQuality();
            var positions = mesh.vertices; var triangles = mesh.triangles;
            report.faces = triangles.Length / 3;
            report.faceAnisotropy = new float[report.faces];
            if (uv == null || uv.Length != positions.Length) { report.invalidFaces = report.faces; return report; }
            double weighted = 0, totalArea = 0;
            for (int f = 0; f < report.faces; ++f) {
                int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
                var e1 = localToWorld.MultiplyVector(positions[b] - positions[a]);
                var e2 = localToWorld.MultiplyVector(positions[c] - positions[a]);
                double length = e1.magnitude, cross = Vector3.Cross(e1, e2).magnitude;
                if (!Finite(length) || !Finite(cross)) { ++report.invalidFaces; continue; }
                if (length < 1e-12 || cross < 1e-18) { ++report.degenerateFaces; continue; }
                double x = Vector3.Dot(e1, e2) / length, y = cross / length;
                var u = Vector2.Scale(uv[b] - uv[a], metric);
                var v = Vector2.Scale(uv[c] - uv[a], metric);
                double j00 = u.x / length, j10 = u.y / length;
                double j01 = (v.x - u.x * x / length) / y, j11 = (v.y - u.y * x / length) / y;
                double aa = j00 * j00 + j10 * j10, bb = j01 * j01 + j11 * j11, ab = j00 * j01 + j10 * j11;
                double determinant = j00 * j11 - j01 * j10;
                double max = (aa + bb + Math.Sqrt(Math.Max(0, (aa - bb) * (aa - bb) + 4 * ab * ab))) * .5;
                double min = max > 0 ? determinant * determinant / max : 0;
                if (!Finite(max) || !Finite(min)) { ++report.invalidFaces; continue; }
                if (min < 1e-30) { ++report.degenerateFaces; report.faceAnisotropy[f] = float.MaxValue; continue; }
                double ratio = Math.Sqrt(max / min);
                report.faceAnisotropy[f] = (float)Math.Min(ratio, float.MaxValue);
                report.worstAnisotropy = Math.Max(report.worstAnisotropy, ratio);
                if (ratio > 1.25) ++report.stretchedFaces;
                weighted += ratio * cross; totalArea += cross;
            }
            report.areaWeightedAnisotropy = totalArea > 0 ? weighted / totalArea : 0;
            // Includes positive-area overlaps within the same shell/source; shared edges are legal.
            var atlas = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry {
                positions = positions, uv = uv, indices = triangles, charts = new int[positions.Length]
            }, CancellationToken.None, comparisonBudget: 2000000);
            report.overlapPairs = atlas.pairs; report.overlapPairArea = atlas.pairAreaSum;
            report.outOfBoundsVertices = atlas.outOfBoundsVertices;
            report.overlapScanComplete = atlas.complete; report.overlapComparisons = atlas.comparisons;
            report.overlapSamples.AddRange(atlas.samples);
            return report;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
