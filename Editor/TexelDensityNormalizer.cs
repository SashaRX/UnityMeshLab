// TexelDensityNormalizer.cs — pre-pack UV0 reshaping in two passes:
//   1) Anisotropic stretch correction (Sander L² metric, SIGGRAPH 2001
//      "Texture Mapping Progressive Meshes"). For each shell we average the
//      metric tensor M = JᵀJ of the UV→3D parameterisation over triangles,
//      weighted by 3D area. Its eigenvalues σ₁² ≥ σ₂² are the principal
//      texture stretches; their ratio is the anisotropy we want to remove.
//      A non-uniform UV scale along the principal directions reduces the
//      ratio to a configurable cap, preserving area by construction
//      (k_along × k_across = 1). Unlike PCA over vertex positions, this is
//      invariant to vertex distribution, shell curvature, and UV-island
//      rotation — it measures the actual UV→3D mapping, not where vertices
//      sit.
//   2) Texel density correction (uniform per-shell scale) bringing UV-area
//      proportional to 3D-area, plus a global coverage budget that leaves
//      slack for xatlas's bin-packing.
//
// Both passes mutate the local uvFlat copy fed to xatlas. mesh.uv is never
// touched (project invariant).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TexelDensityNormalizer
    {
        /// <summary>
        /// Two-pass per-shell UV0 reshape: (1) Sander-metric anisotropy
        /// correction, (2) area-based density correction. Pass 1 runs first so
        /// pass 2 measures UV area on the already-reshaped layout — the result
        /// is uniform texels-per-world-unit AND a UV layout whose per-axis
        /// stretch matches the surface in 3D.
        /// </summary>
        /// <param name="uvFlat">Flat UV0 array (vertexCount * 2 floats). Mutated in place.</param>
        /// <param name="shells">Per-mesh shells (read-only).</param>
        /// <param name="tris">Mesh triangles.</param>
        /// <param name="positions">Mesh vertex positions in mesh-local space.</param>
        /// <param name="scaleMin">Safety clamp on any single-axis scale. Default 0.1.</param>
        /// <param name="scaleMax">Safety clamp on any single-axis scale. Default 10.</param>
        /// <param name="medianDensity">When true the density target is the median per-shell density (robust
        /// against outliers); when false it's the area-weighted average. Default false.</param>
        /// <param name="targetCoverage">After per-shell density normalisation, total UV area is rescaled
        /// to this fraction of [0,1]² so xatlas doesn't overflow the requested atlas resolution due to
        /// bin-packing slack. Default 0.75. ≤0 or ≥1 disables the budget step.</param>
        /// <param name="normalizeAspect">Enable pass 1 (Sander stretch correction). Default true.</param>
        /// <param name="maxAspect">Cap on the post-correction σ₁/σ₂ ratio. Triangles whose mapping is
        /// hopelessly anisotropic (sliver shells, σ₁/σ₂ > 100) would otherwise force runaway non-uniform
        /// UV scale. With cap C: maximum stretch factor along the long axis is √(current_ratio / C),
        /// so the minimum UV dimension shrinks by at most √(C/current_ratio). Default 2.</param>
        /// <returns>Number of shells that received a non-identity transform across both passes.</returns>
        internal static int Normalize(
            float[] uvFlat,
            List<UvShell> shells,
            int[] tris,
            Vector3[] positions,
            float scaleMin = 0.1f,
            float scaleMax = 10f,
            bool medianDensity = false,
            float targetCoverage = 0.75f,
            bool normalizeAspect = true,
            float maxAspect = 2f)
        {
            if (uvFlat == null || shells == null || shells.Count == 0) return 0;
            if (tris == null || positions == null) return 0;
            if (scaleMin <= 0f) scaleMin = 0.1f;
            if (scaleMax < scaleMin) scaleMax = scaleMin;

            int n = shells.Count;
            int uvLen = uvFlat.Length;
            int posLen = positions.Length;
            float aspectClamp = Mathf.Max(1f, maxAspect);
            int modifiedAspect = 0;
            int modifiedDensity = 0;

            // ── PASS 1: anisotropy correction via Sander L² stretch metric ──
            // For each shell, average M = JᵀJ over its triangles (weighted by
            // 3D area). Eigendecompose to get principal UV directions and
            // singular values (3D length per unit UV step). Apply non-uniform
            // UV scale along those directions to bring σ₁/σ₂ down to the cap.
            if (normalizeAspect)
            {
                for (int si = 0; si < n; si++)
                {
                    var shell = shells[si];
                    if (shell?.faceIndices == null || shell.vertexIndices == null) continue;
                    if (shell.vertexIndices.Count < 3) continue;

                    if (!ComputeShellStretchMetric(shell, uvFlat, tris, positions,
                            out var uvCentroid, out var axisU,
                            out float sigmaU, out float sigmaV)) continue;

                    if (sigmaU < 1e-8f || sigmaV < 1e-8f) continue;

                    // sigmaU ≥ sigmaV by construction (eigenvalue ordering).
                    float currentRatio = sigmaU / sigmaV;
                    if (currentRatio <= aspectClamp + 1e-3f) continue;

                    // Stretch UV along axisU by kU (long axis becomes longer in UV);
                    // shrink along axisV by kV = 1/kU. Triangle area preserved.
                    // Target post-correction ratio = aspectClamp.
                    //   (sigmaU / kU) / (sigmaV / kV) = aspectClamp
                    //   with kU·kV = 1  →  kU² = currentRatio / aspectClamp
                    float kU = Mathf.Sqrt(currentRatio / aspectClamp);
                    float kV = 1f / kU;
                    kU = Mathf.Clamp(kU, scaleMin, scaleMax);
                    kV = Mathf.Clamp(kV, scaleMin, scaleMax);
                    if (Mathf.Abs(kU - 1f) < 1e-3f) continue;

                    Vector2 axisV = new Vector2(-axisU.y, axisU.x);
                    foreach (int v in shell.vertexIndices)
                    {
                        int idx = v * 2;
                        if ((uint)(idx + 1) >= (uint)uvLen) continue;
                        float dx = uvFlat[idx]     - uvCentroid.x;
                        float dy = uvFlat[idx + 1] - uvCentroid.y;
                        float a1 = dx * axisU.x + dy * axisU.y;
                        float a2 = dx * axisV.x + dy * axisV.y;
                        a1 *= kU;
                        a2 *= kV;
                        uvFlat[idx]     = uvCentroid.x + a1 * axisU.x + a2 * axisV.x;
                        uvFlat[idx + 1] = uvCentroid.y + a1 * axisU.y + a2 * axisV.y;
                    }
                    modifiedAspect++;
                }
            }

            // ── PASS 2: density correction ──
            // Re-measure UV area on the (possibly aspect-corrected) layout, then
            // apply per-shell uniform scale so UV-area / 3D-area is constant
            // across all shells, modulated by the coverage budget.
            var area3DPerShell = new double[n];
            var areaUVPerShell = new double[n];
            double sumArea3D = 0.0;
            double sumAreaUV = 0.0;

            for (int si = 0; si < n; si++)
            {
                var shell = shells[si];
                if (shell.faceIndices == null) continue;
                double a3 = 0.0, au = 0.0;
                foreach (int f in shell.faceIndices)
                {
                    int t = f * 3;
                    if ((uint)(t + 2) >= (uint)tris.Length) continue;
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                    Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                    a3 += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;

                    int u0 = i0 * 2, u1 = i1 * 2, u2 = i2 * 2;
                    if ((uint)(u0 + 1) >= (uint)uvLen ||
                        (uint)(u1 + 1) >= (uint)uvLen ||
                        (uint)(u2 + 1) >= (uint)uvLen) continue;
                    double ax = uvFlat[u0],     ay = uvFlat[u0 + 1];
                    double bx = uvFlat[u1],     by = uvFlat[u1 + 1];
                    double cx = uvFlat[u2],     cy = uvFlat[u2 + 1];
                    au += Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
                }
                area3DPerShell[si] = a3;
                areaUVPerShell[si] = au;
                sumArea3D += a3;
                sumAreaUV += au;
            }

            if (sumArea3D < 1e-12 || sumAreaUV < 1e-12) return modifiedAspect;

            double densityTarget;
            if (medianDensity)
            {
                var densities = new List<double>(n);
                for (int si = 0; si < n; si++)
                {
                    double a3 = area3DPerShell[si];
                    double au = areaUVPerShell[si];
                    if (a3 > 1e-12 && au > 1e-12)
                        densities.Add(au / a3);
                }
                if (densities.Count == 0) return modifiedAspect;
                densities.Sort();
                densityTarget = densities[densities.Count / 2];
            }
            else
            {
                densityTarget = sumAreaUV / sumArea3D;
            }
            if (densityTarget < 1e-12) return modifiedAspect;

            // Coverage budget: scale density target so total post-normalize UV
            // area equals targetCoverage fraction of [0,1]².
            if (targetCoverage > 0f && targetCoverage < 1f && sumArea3D > 1e-12)
                densityTarget = targetCoverage / sumArea3D;

            for (int si = 0; si < n; si++)
            {
                double a3 = area3DPerShell[si];
                double au = areaUVPerShell[si];
                if (a3 < 1e-12 || au < 1e-12) continue;

                double desired = a3 * densityTarget;
                double scaleSq = desired / au;
                if (double.IsNaN(scaleSq) || double.IsInfinity(scaleSq) || scaleSq <= 0.0) continue;
                float scale = (float)Math.Sqrt(scaleSq);
                if (float.IsNaN(scale) || float.IsInfinity(scale)) continue;
                scale = Mathf.Clamp(scale, scaleMin, scaleMax);
                if (Mathf.Abs(scale - 1f) < 1e-4f) continue;

                var shell = shells[si];
                if (shell.vertexIndices == null || shell.vertexIndices.Count == 0) continue;

                // Recompute the UV centroid AFTER pass 1 (shell.boundsMin/Max are
                // pre-aspect and stale). Centroid stays put under uniform scale,
                // so this also keeps the shell's center where pass 1 left it.
                Vector2 c = Vector2.zero;
                int cn = 0;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    c.x += uvFlat[idx];
                    c.y += uvFlat[idx + 1];
                    cn++;
                }
                if (cn == 0) continue;
                c.x /= cn;
                c.y /= cn;

                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    uvFlat[idx]     = c.x + (uvFlat[idx]     - c.x) * scale;
                    uvFlat[idx + 1] = c.y + (uvFlat[idx + 1] - c.y) * scale;
                }
                modifiedDensity++;
            }

            return Mathf.Max(modifiedAspect, modifiedDensity);
        }

        // ── Sander L² stretch metric ──

        /// <summary>
        /// Computes the shell-averaged metric tensor M = JᵀJ of the UV0→3D
        /// parameterisation, weighted by 3D triangle area, then eigendecomposes
        /// the 2×2 result.
        ///
        /// For triangle with 3D vertices p0,p1,p2 and UV vertices q0,q1,q2 the
        /// affine UV→3D map has Jacobian J (3×2) defined by:
        ///   J·(q1-q0) = p1-p0   and   J·(q2-q0) = p2-p0
        /// Solving:
        ///   ∂P/∂u =  ( (q2.y-q0.y)·(p1-p0) - (q1.y-q0.y)·(p2-p0) ) / detUV
        ///   ∂P/∂v =  (-(q2.x-q0.x)·(p1-p0) + (q1.x-q0.x)·(p2-p0) ) / detUV
        /// where detUV is twice the signed UV-area of the triangle.
        ///
        /// The 2×2 symmetric M_tri = JᵀJ has eigenvalues σ₁² ≥ σ₂² that are
        /// the squared principal stretches (3D length / UV unit) along
        /// orthogonal UV directions — the singular values of J in 2D parlance.
        /// Averaging area-weighted M_tri over the shell gives the Sander L²
        /// shell metric.
        /// </summary>
        static bool ComputeShellStretchMetric(
            UvShell shell, float[] uvFlat, int[] tris, Vector3[] positions,
            out Vector2 uvCentroid, out Vector2 axisU,
            out float sigmaU, out float sigmaV)
        {
            uvCentroid = Vector2.zero;
            axisU = new Vector2(1, 0);
            sigmaU = 0f; sigmaV = 0f;
            if (shell?.faceIndices == null || shell.vertexIndices == null) return false;

            int posLen = positions.Length;
            int uvLen = uvFlat.Length;

            // UV centroid (used by the caller to apply the non-uniform scale).
            double sx = 0, sy = 0;
            int count = 0;
            foreach (int v in shell.vertexIndices)
            {
                int idx = v * 2;
                if ((uint)(idx + 1) >= (uint)uvLen) continue;
                sx += uvFlat[idx];
                sy += uvFlat[idx + 1];
                count++;
            }
            if (count == 0) return false;
            uvCentroid = new Vector2((float)(sx / count), (float)(sy / count));

            // Area-weighted accumulator of M = JᵀJ.
            double M00 = 0, M01 = 0, M11 = 0;
            double totalArea3D = 0;

            foreach (int f in shell.faceIndices)
            {
                int t = f * 3;
                if ((uint)(t + 2) >= (uint)tris.Length) continue;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                int u0 = i0 * 2, u1 = i1 * 2, u2 = i2 * 2;
                if ((uint)(u0 + 1) >= (uint)uvLen ||
                    (uint)(u1 + 1) >= (uint)uvLen ||
                    (uint)(u2 + 1) >= (uint)uvLen) continue;

                Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                Vector3 e1 = p1 - p0, e2 = p2 - p0;
                double area3D = Vector3.Cross(e1, e2).magnitude * 0.5;
                if (area3D < 1e-14) continue;

                double q0x = uvFlat[u0],     q0y = uvFlat[u0 + 1];
                double q1x = uvFlat[u1],     q1y = uvFlat[u1 + 1];
                double q2x = uvFlat[u2],     q2y = uvFlat[u2 + 1];
                double d1x = q1x - q0x, d1y = q1y - q0y;
                double d2x = q2x - q0x, d2y = q2y - q0y;
                double det = d1x * d2y - d2x * d1y;
                if (Math.Abs(det) < 1e-14) continue;
                double invDet = 1.0 / det;

                // ∂P/∂u = ( d2y·e1 - d1y·e2 ) · invDet
                // ∂P/∂v = (-d2x·e1 + d1x·e2 ) · invDet
                double ku1 =  d2y * invDet, ku2 = -d1y * invDet;
                double kv1 = -d2x * invDet, kv2 =  d1x * invDet;
                double dPu_x = ku1 * e1.x + ku2 * e2.x;
                double dPu_y = ku1 * e1.y + ku2 * e2.y;
                double dPu_z = ku1 * e1.z + ku2 * e2.z;
                double dPv_x = kv1 * e1.x + kv2 * e2.x;
                double dPv_y = kv1 * e1.y + kv2 * e2.y;
                double dPv_z = kv1 * e1.z + kv2 * e2.z;

                double m00 = dPu_x * dPu_x + dPu_y * dPu_y + dPu_z * dPu_z;
                double m11 = dPv_x * dPv_x + dPv_y * dPv_y + dPv_z * dPv_z;
                double m01 = dPu_x * dPv_x + dPu_y * dPv_y + dPu_z * dPv_z;

                M00 += area3D * m00;
                M01 += area3D * m01;
                M11 += area3D * m11;
                totalArea3D += area3D;
            }

            if (totalArea3D < 1e-14) return false;
            M00 /= totalArea3D;
            M01 /= totalArea3D;
            M11 /= totalArea3D;

            // Eigendecompose 2×2 symmetric [M00 M01; M01 M11].
            double tr = M00 + M11;
            double det2 = M00 * M11 - M01 * M01;
            double disc = Math.Sqrt(Math.Max(0.0, tr * tr * 0.25 - det2));
            double lambda1 = tr * 0.5 + disc;          // larger eigenvalue
            double lambda2 = tr * 0.5 - disc;          // smaller eigenvalue
            if (lambda2 < 0) lambda2 = 0;

            // Eigenvector for λ₁ lives in UV space and gives the principal
            // stretch direction. Standard form: (M01, λ₁ - M00) when M01 ≠ 0;
            // axis-aligned fallback when the metric tensor is diagonal.
            if (Math.Abs(M01) > 1e-14)
            {
                Vector2 v = new Vector2((float)M01, (float)(lambda1 - M00));
                if (v.sqrMagnitude < 1e-14f) v = new Vector2(1, 0);
                axisU = v.normalized;
            }
            else
            {
                axisU = (M00 >= M11) ? new Vector2(1, 0) : new Vector2(0, 1);
            }
            sigmaU = (float)Math.Sqrt(lambda1);
            sigmaV = (float)Math.Sqrt(lambda2);
            return true;
        }
    }
}
