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
            for (int f = 0; f < report.faces; ++f)
                MeasureFace(report, f, positions, triangles, uv, metric, localToWorld, ref weighted, ref totalArea);
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

        static void MeasureFace(TransferUvQuality report, int f, Vector3[] positions, int[] triangles, Vector2[] uv,
            Vector2 metric, Matrix4x4 localToWorld, ref double weighted, ref double totalArea)
        {
            int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
            var frame=WorldFrame(positions[a],positions[b],positions[c],localToWorld);
            double length=frame.length;
            if (!Finite(length)) { ++report.invalidFaces; return; }
            if (length < 1e-12) { ++report.degenerateFaces; return; }
            double cross=length*frame.y;
            if (!Finite(cross)) { ++report.invalidFaces; return; }
            if (cross < 1e-18) { ++report.degenerateFaces; return; }
            double ux=((double)uv[b].x-uv[a].x)*metric.x,uy=((double)uv[b].y-uv[a].y)*metric.y;
            double vx=((double)uv[c].x-uv[a].x)*metric.x,vy=((double)uv[c].y-uv[a].y)*metric.y;
            double j00=ux/length,j10=uy/length;
            double j01=(vx-ux*frame.x/length)/frame.y,j11=(vy-uy*frame.x/length)/frame.y;
            double aa = j00 * j00 + j10 * j10, bb = j01 * j01 + j11 * j11, ab = j00 * j01 + j10 * j11;
            double determinant = j00 * j11 - j01 * j10;
            double max = (aa + bb + Math.Sqrt(Math.Max(0, (aa - bb) * (aa - bb) + 4 * ab * ab))) * .5;
            double min = max > 0 ? determinant * determinant / max : 0;
            if (!Finite(max) || !Finite(min)) { ++report.invalidFaces; return; }
            if (min < 1e-30) { ++report.degenerateFaces; report.faceAnisotropy[f] = float.MaxValue; return; }
            double ratio = Math.Sqrt(max / min);
            report.faceAnisotropy[f] = (float)Math.Min(ratio, float.MaxValue);
            report.worstAnisotropy = Math.Max(report.worstAnisotropy, ratio);
            if (ratio > 1.25) ++report.stretchedFaces;
            weighted += ratio * cross; totalArea += cross;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // Promote stored Float32 corners before subtracting and transforming.
        // Float32 dot products add false shear to nearly collinear triangles.
        // The packer and exported-mesh audit use the same intrinsic frame.
        internal static (double length,double x,double y) WorldFrame(Vector3 a,Vector3 b,Vector3 c,Matrix4x4 matrix)
        {
            double ex=(double)b.x-a.x,ey=(double)b.y-a.y,ez=(double)b.z-a.z;
            double dx=(double)c.x-a.x,dy=(double)c.y-a.y,dz=(double)c.z-a.z;
            return ReverseUvTransfer.TriangleFrame(matrix.m00*ex+matrix.m01*ey+matrix.m02*ez,
                matrix.m10*ex+matrix.m11*ey+matrix.m12*ez,matrix.m20*ex+matrix.m21*ey+matrix.m22*ez,
                matrix.m00*dx+matrix.m01*dy+matrix.m02*dz,matrix.m10*dx+matrix.m11*dy+matrix.m12*dz,
                matrix.m20*dx+matrix.m21*dy+matrix.m22*dz);
        }

        internal static double TriangleAnisotropy(Vector3 e1, Vector3 e2, Vector2 a, Vector2 b, Vector2 c)
        {
            var frame=ReverseUvTransfer.TriangleFrame(e1.x,e1.y,e1.z,e2.x,e2.y,e2.z);
            if(!Finite(frame.length) || !Finite(frame.y) || frame.length<1e-12 || frame.length*frame.y<1e-18)
                return double.PositiveInfinity;
            return ReverseUvTransfer.TriangleAnisotropy(frame,a,b,c);
        }
    }
}
