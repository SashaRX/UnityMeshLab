using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One target shell's share of the furniture-corpus metrics (<see cref="TransferUvQuality"/>
    /// and exact zero-area UV faces) in one UV2 layout, so that two layouts of the same shell can
    /// be compared the way the corpus report compares two runs, without trading one metric for another.</summary>
    internal sealed class ShellLayoutScore
    {
        internal int invalidFaces, degenerateFaces, zeroAreaFaces, outOfBoundsVertices, overlapPairs;
        internal double worstAnisotropy, weightedAnisotropy, weightedArea, overlapArea;
        internal bool overlapScanComplete;

        /// <summary>Scores the shell's <paramref name="faces"/> and <paramref name="vertices"/> in
        /// <paramref name="uv"/>. Overlaps are the shell's pairs with itself and with
        /// <paramref name="neighbours"/>, which must hold every other face that could touch it;
        /// pairs among the neighbours are left out, as they are the same in both layouts.</summary>
        internal static ShellLayoutScore Measure(List<int> faces, IEnumerable<int> vertices, List<int> neighbours,
            int[] triangles, Vector3[] positions, Vector2[] uv)
        {
            var score = new ShellLayoutScore();
            foreach (int face in faces) score.AddFace(face, triangles, positions, uv);
            foreach (int vertex in vertices)
                if (OutOfBounds(uv[vertex])) ++score.outOfBoundsVertices;
            score.ScanOverlap(faces, neighbours, triangles, uv);
            return score;
        }

        /// <summary>The stretch totals of every face, the whole-mesh area-weighted anisotropy's
        /// share that a shell's score is weighed against.</summary>
        internal static ShellLayoutScore MeasureStretch(int[] triangles, Vector3[] positions, Vector2[] uv)
        {
            var score = new ShellLayoutScore();
            for (int face = 0; face < triangles.Length / 3; ++face) score.AddFace(face, triangles, positions, uv);
            return score;
        }

        /// <summary>True when <paramref name="layout"/> is no worse than <paramref name="other"/> in any
        /// metric and better in one, by the corpus report's rule: integers exactly, real values
        /// within a relative 1e-6. The area-weighted anisotropy is the whole mesh's, with
        /// <paramref name="restWeighted"/> and <paramref name="restArea"/> the other faces' share.
        /// An incomplete overlap scan dominates nothing and is dominated by nothing.</summary>
        internal static bool Dominates(ShellLayoutScore layout, ShellLayoutScore other, double restWeighted, double restArea)
        {
            if (!layout.overlapScanComplete || !other.overlapScanComplete) return false;
            int[] verdicts = {
                layout.invalidFaces.CompareTo(other.invalidFaces),
                layout.degenerateFaces.CompareTo(other.degenerateFaces),
                layout.zeroAreaFaces.CompareTo(other.zeroAreaFaces),
                layout.outOfBoundsVertices.CompareTo(other.outOfBoundsVertices),
                layout.overlapPairs.CompareTo(other.overlapPairs),
                Compare(layout.overlapArea, other.overlapArea),
                Compare(layout.worstAnisotropy, other.worstAnisotropy),
                Compare(Mean(restWeighted + layout.weightedAnisotropy, restArea + layout.weightedArea),
                    Mean(restWeighted + other.weightedAnisotropy, restArea + other.weightedArea)),
            };
            return Array.IndexOf(verdicts, 1) < 0 && Array.IndexOf(verdicts, -1) >= 0;
        }

        // Classified as TransferUvQuality's per-face measure does, in the mesh's local frame.
        void AddFace(int face, int[] triangles, Vector3[] positions, Vector2[] uv)
        {
            int a = triangles[face * 3], b = triangles[face * 3 + 1], c = triangles[face * 3 + 2];
            double area = ((double)uv[b].x - uv[a].x) * ((double)uv[c].y - uv[a].y)
                - ((double)uv[b].y - uv[a].y) * ((double)uv[c].x - uv[a].x);
            if (area == 0) ++zeroAreaFaces;
            var frame = TransferUvQuality.WorldFrame(positions[a], positions[b], positions[c], Matrix4x4.identity);
            if (!Finite(frame.length)) { ++invalidFaces; return; }
            if (frame.length <= 0) { ++degenerateFaces; return; }
            double cross = frame.length * frame.y;
            if (!Finite(cross)) { ++invalidFaces; return; }
            if (cross <= 0) { ++degenerateFaces; return; }
            double ratio = TransferUvQuality.TriangleAnisotropy(positions[b] - positions[a], positions[c] - positions[a],
                uv[a], uv[b], uv[c]);
            if (double.IsNaN(ratio)) { ++invalidFaces; return; }
            if (double.IsInfinity(ratio)) { ++degenerateFaces; return; }
            worstAnisotropy = Math.Max(worstAnisotropy, ratio);
            weightedAnisotropy += ratio * cross; weightedArea += cross;
        }

        void ScanOverlap(List<int> faces, List<int> neighbours, int[] triangles, Vector2[] uv)
        {
            // Each face gets its own corners and chart: the shell's faces are charts 1..n and the
            // neighbours share chart 0, so the cross-chart scan counts the shell's pairs with
            // itself and with its neighbours and skips the pairs among the neighbours.
            int count = faces.Count + neighbours.Count;
            var points = new Vector2[count * 3];
            var charts = new int[count * 3];
            var indices = new int[count * 3];
            for (int i = 0; i < count; ++i)
            {
                int face = i < faces.Count ? faces[i] : neighbours[i - faces.Count];
                for (int corner = 0; corner < 3; ++corner)
                {
                    int slot = i * 3 + corner;
                    points[slot] = uv[triangles[face * 3 + corner]];
                    charts[slot] = i < faces.Count ? i + 1 : 0;
                    indices[slot] = slot;
                }
            }
            var atlas = UvAtlasDiagnostics.Measure(new RemeshNative.Geometry { uv = points, indices = indices, charts = charts },
                CancellationToken.None, comparisonBudget: 2000000, crossChartOnly: true);
            overlapPairs = atlas.pairs; overlapArea = atlas.pairAreaSum; overlapScanComplete = atlas.complete;
        }

        static int Compare(double value, double original)
        {
            double tolerance = 1e-6 * Math.Max(Math.Max(Math.Abs(value), Math.Abs(original)), 1e-12);
            return value < original - tolerance ? -1 : value > original + tolerance ? 1 : 0;
        }

        static double Mean(double weighted, double area) => area > 0 ? weighted / area : 0;

        // The atlas audit's bounds: a finite coordinate outside [0, 1] by more than 1e-4.
        static bool OutOfBounds(Vector2 uv) => Finite(uv.x) && Finite(uv.y)
            && (uv.x < -1e-4f || uv.x > 1.0001f || uv.y < -1e-4f || uv.y > 1.0001f);

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
