// ShellQuality.cs — Per-shell UV parameterization quality metrics.
// Sander L² stretch (Sander/Snyder/Gortler/Hoppe 2001) is the industry-
// standard area-weighted RMS of per-triangle isometric distortion.
// 1.0 = isometric; 1.5 = mild; 2.0+ = noticeable; 3.0+ = severe.

using UnityEngine;
using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    internal static class ShellQuality
    {
        /// <summary>
        /// Sander L² stretch for a single shell (area-weighted RMS of
        /// per-triangle √((σ1² + σ2²) / 2), where σ1,σ2 are singular values
        /// of the Jacobian UV → local-3D-tangent-plane for each triangle).
        /// Returns 1.0 if all triangles are isometric, &gt;1 otherwise.
        /// Returns float.PositiveInfinity if any triangle has degenerate UV
        /// (zero UV area but non-zero 3D area — infinite stretch).
        /// Returns float.NaN if shell has no usable triangles.
        /// </summary>
        internal static float ComputeL2Stretch(
            UvShell shell,
            int[] globalTris,
            Vector3[] positions,
            float[] uvFlat)
        {
            if (shell?.faceIndices == null) return float.NaN;
            int uvLen = uvFlat.Length;
            int posLen = positions.Length;

            double sumArea = 0.0;
            double sumAreaTimesS2 = 0.0;
            int countValid = 0;
            bool hasInfinite = false;

            foreach (int faceIdx in shell.faceIndices)
            {
                int t0 = faceIdx * 3;
                if ((uint)(t0 + 2) >= (uint)globalTris.Length) continue;
                int i0 = globalTris[t0];
                int i1 = globalTris[t0 + 1];
                int i2 = globalTris[t0 + 2];
                if ((uint)i0 >= (uint)posLen) continue;
                if ((uint)i1 >= (uint)posLen) continue;
                if ((uint)i2 >= (uint)posLen) continue;
                int u0 = i0 * 2, u1 = i1 * 2, u2 = i2 * 2;
                if ((uint)(u0 + 1) >= (uint)uvLen) continue;
                if ((uint)(u1 + 1) >= (uint)uvLen) continue;
                if ((uint)(u2 + 1) >= (uint)uvLen) continue;

                Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                Vector2 uvA = new Vector2(uvFlat[u0], uvFlat[u0 + 1]);
                Vector2 uvB = new Vector2(uvFlat[u1], uvFlat[u1 + 1]);
                Vector2 uvC = new Vector2(uvFlat[u2], uvFlat[u2 + 1]);

                // 3D area
                Vector3 e1_3D = p1 - p0;
                Vector3 e2_3D = p2 - p0;
                Vector3 cross3D = Vector3.Cross(e1_3D, e2_3D);
                float area3DTimes2 = cross3D.magnitude;
                if (area3DTimes2 < 1e-12f) continue;  // degenerate 3D triangle

                // UV signed area
                Vector2 e1_UV = uvB - uvA;
                Vector2 e2_UV = uvC - uvA;
                float areaUVTimes2 = Mathf.Abs(e1_UV.x * e2_UV.y - e1_UV.y * e2_UV.x);

                // Project 3D triangle into its own tangent plane (q0=0, q1 along x).
                float len_e1_3D = e1_3D.magnitude;
                Vector3 e1hat = e1_3D / len_e1_3D;
                Vector3 nhat = cross3D / area3DTimes2;
                Vector3 e2hat = Vector3.Cross(nhat, e1hat);
                Vector2 q1 = new Vector2(len_e1_3D, 0f);
                Vector2 q2 = new Vector2(Vector3.Dot(e2_3D, e1hat), Vector3.Dot(e2_3D, e2hat));

                if (areaUVTimes2 < 1e-12f)
                {
                    // UV degenerate but 3D not → infinite stretch
                    hasInfinite = true;
                    sumArea += 0.5 * area3DTimes2;
                    continue;
                }

                // J: 2x2 matrix such that J * (uvB - uvA) = q1 - 0, J * (uvC - uvA) = q2 - 0.
                // Solve via direct inversion of 2x2.
                // [J11 J12] [e1_UV.x] = q1.x = len_e1_3D
                // [J21 J22] [e1_UV.y]   0
                // [J11 J12] [e2_UV.x] = q2.x
                // [J21 J22] [e2_UV.y]   q2.y
                //
                // Stack columns of UV edges into M_UV (2x2), same for M_q (2x2).
                // J · M_UV = M_q  →  J = M_q · M_UV^(-1)
                float mUV_det = e1_UV.x * e2_UV.y - e1_UV.y * e2_UV.x;
                if (Mathf.Abs(mUV_det) < 1e-12f)
                {
                    hasInfinite = true;
                    sumArea += 0.5 * area3DTimes2;
                    continue;
                }
                float inv = 1f / mUV_det;
                // M_UV^(-1) = (1/det) * [[e2.y, -e2.x],[-e1.y, e1.x]]
                float invUV_a =  e2_UV.y * inv;
                float invUV_b = -e2_UV.x * inv;
                float invUV_c = -e1_UV.y * inv;
                float invUV_d =  e1_UV.x * inv;
                // M_q = [[q1.x, q2.x], [q1.y, q2.y]] = [[len, q2.x], [0, q2.y]]
                float J11 = len_e1_3D * invUV_a + q2.x * invUV_c;
                float J12 = len_e1_3D * invUV_b + q2.x * invUV_d;
                float J21 = 0f          + q2.y * invUV_c;
                float J22 = 0f          + q2.y * invUV_d;

                // 2x2 SVD: singular values σ1,σ2 of J = sqrt(eigenvalues of J^T J).
                // J^T J entries:
                float a = J11 * J11 + J21 * J21;  // (J^T J)[0,0]
                float b = J11 * J12 + J21 * J22;  // (J^T J)[0,1] == [1,0]
                float c = J12 * J12 + J22 * J22;  // (J^T J)[1,1]
                // Eigenvalues of [[a,b],[b,c]]: (a+c)/2 ± sqrt(((a-c)/2)² + b²)
                float tr = a + c;
                float disc = (a - c) * (a - c) * 0.25f + b * b;
                float sqdisc = Mathf.Sqrt(Mathf.Max(0f, disc));
                float lam1 = tr * 0.5f + sqdisc;
                float lam2 = Mathf.Max(0f, tr * 0.5f - sqdisc);

                // Triangle stretch² = (σ1² + σ2²) / 2 = (lam1 + lam2) / 2 = tr / 2
                float triS2 = tr * 0.5f;

                double a3d = 0.5 * area3DTimes2;
                sumArea += a3d;
                sumAreaTimesS2 += a3d * triS2;
                countValid++;
            }

            if (countValid == 0)
            {
                if (hasInfinite) return float.PositiveInfinity;
                return float.NaN;
            }

            float l2 = (float)System.Math.Sqrt(sumAreaTimesS2 / sumArea);
            if (hasInfinite) return Mathf.Max(l2, 1000f);  // signal heavy stretch
            return l2;
        }
    }
}
