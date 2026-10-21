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

        /// <summary>Audits per-face metric stretch and atlas contacts without modifying the mesh or UV buffer.</summary>
        internal static TransferUvQuality Measure(Mesh mesh, Vector2[] uv, Vector2 metric, Matrix4x4 localToWorld)
        {
            var positions = mesh.vertices; var triangles = mesh.triangles;
            var report = MeasureTriangles(positions, triangles, uv, metric, localToWorld);
            if (uv == null || uv.Length != positions.Length) return report;
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

        /// <summary>Measures intrinsic per-face metrics on plain arrays, independently of the separate atlas-contact audit.</summary>
        internal static TransferUvQuality MeasureTriangles(Vector3[] positions, int[] triangles, Vector2[] uv,
            Vector2 metric, Matrix4x4 localToWorld)
        {
            var report = new TransferUvQuality();
            report.faces = triangles.Length / 3;
            report.faceAnisotropy = new float[report.faces];
            if (uv == null || uv.Length != positions.Length) { report.invalidFaces = report.faces; return report; }
            double weighted = 0, totalArea = 0;
            for (int f = 0; f < report.faces; ++f)
                MeasureFace(report, f, positions, triangles, uv, metric, localToWorld, ref weighted, ref totalArea);
            report.areaWeightedAnisotropy = totalArea > 0 ? weighted / totalArea : 0;
            return report;
        }

        /// <summary>Classifies one face and accumulates finite stretch using its transformed geometry area.</summary>
        static void MeasureFace(TransferUvQuality report, int f, Vector3[] positions, int[] triangles, Vector2[] uv,
            Vector2 metric, Matrix4x4 localToWorld, ref double weighted, ref double totalArea)
        {
            int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
            var frame=WorldFrame(positions[a],positions[b],positions[c],localToWorld);
            double length=frame.length;
            if (!Finite(length)) { ++report.invalidFaces; return; }
            if (length <= 0) { ++report.degenerateFaces; return; }
            double cross=length*frame.y;
            if (!Finite(cross)) { ++report.invalidFaces; return; }
            if (cross <= 0) { ++report.degenerateFaces; return; }
            double ratio = UvAnisotropy(frame, uv[a], uv[b], uv[c], metric);
            if (double.IsNaN(ratio)) { ++report.invalidFaces; return; }
            if (double.IsInfinity(ratio)) { ++report.degenerateFaces; report.faceAnisotropy[f] = float.MaxValue; return; }
            report.faceAnisotropy[f] = (float)Math.Min(ratio, float.MaxValue);
            report.worstAnisotropy = Math.Max(report.worstAnisotropy, ratio);
            if (ratio > 1.25) ++report.stretchedFaces;
            weighted += ratio * cross; totalArea += cross;
        }

        /// <summary>Checks stored UV degeneracy before frame conversion, preserving nonfinite-input classification.</summary>
        static double UvAnisotropy((double length, double x, double y) frame, Vector2 a, Vector2 b, Vector2 c, Vector2 metric)
        {
            double ux = (double)b.x - a.x, uy = (double)b.y - a.y;
            double vx = (double)c.x - a.x, vy = (double)c.y - a.y;
            if (!Finite(ux) || !Finite(uy) || !Finite(vx) || !Finite(vy) || !Finite(metric.x) || !Finite(metric.y))
                return double.NaN;
            // Test stored UVs before metric scaling and geometry shear: rounding those
            // operations can turn a zero determinant into a tiny nonzero value.
            // No area epsilon: very thin but nonzero faces still have measurable stretch.
            if (ux * vy - uy * vx == 0) return double.PositiveInfinity;
            ux *= metric.x; uy *= metric.y; vx *= metric.x; vy *= metric.y;
            return JacobianAnisotropy(ux / frame.length, uy / frame.length,
                (vx - ux * frame.x / frame.length) / frame.y,
                (vy - uy * frame.x / frame.length) / frame.y);
        }

        /// <summary>Normalizes the Jacobian before squaring so nonsingular stretch is independent of uniform scale.</summary>
        static double JacobianAnisotropy(double j00, double j10, double j01, double j11)
        {
            if (!Finite(j00) || !Finite(j10) || !Finite(j01) || !Finite(j11)) return double.NaN;
            double scale = Math.Max(Math.Max(Math.Abs(j00), Math.Abs(j10)), Math.Max(Math.Abs(j01), Math.Abs(j11)));
            if (scale <= 0) return double.PositiveInfinity;
            j00 /= scale; j10 /= scale; j01 /= scale; j11 /= scale;
            double aa = j00 * j00 + j10 * j10, bb = j01 * j01 + j11 * j11, ab = j00 * j01 + j10 * j11;
            double determinant = j00 * j11 - j01 * j10;
            double max = (aa + bb + Math.Sqrt(Math.Max(0, (aa - bb) * (aa - bb) + 4 * ab * ab))) * .5;
            double min = max > 0 ? determinant * determinant / max : 0;
            return min > 0 ? Math.Sqrt(max / min) : double.PositiveInfinity;
        }

        /// <summary>Separates nonfinite input from finite values that may represent a collapsed face.</summary>
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Builds the shared intrinsic frame in double precision to avoid Float32 shear on thin triangles.</summary>
        internal static (double length,double x,double y) WorldFrame(Vector3 a,Vector3 b,Vector3 c,Matrix4x4 matrix)
        {
            double ex=(double)b.x-a.x,ey=(double)b.y-a.y,ez=(double)b.z-a.z;
            double dx=(double)c.x-a.x,dy=(double)c.y-a.y,dz=(double)c.z-a.z;
            return ReverseUvTransfer.TriangleFrame(matrix.m00*ex+matrix.m01*ey+matrix.m02*ez,
                matrix.m10*ex+matrix.m11*ey+matrix.m12*ez,matrix.m20*ex+matrix.m21*ey+matrix.m22*ez,
                matrix.m00*dx+matrix.m01*dy+matrix.m02*dz,matrix.m10*dx+matrix.m11*dy+matrix.m12*dz,
                matrix.m20*dx+matrix.m21*dy+matrix.m22*dz);
        }

        /// <summary>Measures UV stretch against two geometry edges, rejecting singular geometry and stored UVs.</summary>
        internal static double TriangleAnisotropy(Vector3 e1, Vector3 e2, Vector2 a, Vector2 b, Vector2 c)
        {
            var frame=ReverseUvTransfer.TriangleFrame(e1.x,e1.y,e1.z,e2.x,e2.y,e2.z);
            if (!Finite(frame.length) || !Finite(frame.y) || frame.length <= 0 || frame.y <= 0)
                return double.PositiveInfinity;
            return UvAnisotropy(frame, a, b, c, Vector2.one);
        }
    }
}
