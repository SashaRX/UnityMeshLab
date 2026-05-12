// TexelDensityNormalizer.cs — pre-pack UV0 reshaping in two passes:
//   1) Aspect correction (PCA-based, area-preserving) along the shell's
//      principal UV directions. Fixes shells whose UV0 was authored with the
//      wrong proportions vs the real-world surface, even when the UV layout
//      isn't axis-aligned (diagonal/rotated UV islands are handled correctly
//      because PCA finds the true principal axes, unlike AABB).
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
        /// Two-pass per-shell UV0 reshape: (1) PCA-based aspect correction,
        /// (2) area-based density correction. Pass 1 runs first so that pass 2
        /// measures UV area on the already-aspect-corrected shape — produces
        /// uniform texels-per-world-unit AND a UV layout whose per-axis stretch
        /// matches the surface in 3D.
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
        /// <param name="normalizeAspect">Enable pass 1 (aspect correction). Default true.</param>
        /// <param name="maxAspect">Cap on the 3D-side aspect ratio target. Most real surfaces are
        /// near-square; values above ~2 mean the surface is a strip or ribbon and the cap protects
        /// the UV layout from being stretched into a needle. Default 2.0.</param>
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

            // ── PASS 1: aspect correction ──
            // Per-shell, run PCA on the UV0 vertex positions and on the 3D vertex
            // positions projected onto the shell's mean tangent plane. The ratio
            // of the principal std-devs (σ1/σ2) is the "true" anisotropy of each
            // shape regardless of how the UV island is rotated. We then apply a
            // non-uniform scale along the UV principal axes — sigma1 *= sqrt(target/current),
            // sigma2 /= sqrt(target/current) — so triangle area is preserved exactly.
            if (normalizeAspect)
            {
                for (int si = 0; si < n; si++)
                {
                    var shell = shells[si];
                    if (shell.faceIndices == null || shell.vertexIndices == null) continue;
                    if (shell.vertexIndices.Count < 3) continue;

                    if (!ComputeShellUvPca(shell, uvFlat, out var uvCentroid, out var uvAxis1,
                                           out float uvSigma1, out float uvSigma2)) continue;
                    if (uvSigma1 < 1e-6f || uvSigma2 < 1e-6f) continue;

                    if (!ComputeShell3DInPlanePca(shell, tris, positions, out float s3D_1, out float s3D_2)) continue;
                    if (s3D_1 < 1e-6f || s3D_2 < 1e-6f) continue;

                    float aspectUV = uvSigma1 / uvSigma2;
                    float aspect3D = s3D_1 / s3D_2;
                    if (aspect3D > aspectClamp) aspect3D = aspectClamp;
                    if (aspect3D < 1f) aspect3D = 1f;

                    // corr = sqrt(target_aspect / current_aspect)
                    // scaleAlong_σ1 = corr  (long axis becomes longer or shorter as needed)
                    // scaleAlong_σ2 = 1/corr
                    // Product == 1 → triangle area exactly preserved.
                    float corr = Mathf.Sqrt(aspect3D / Mathf.Max(1e-6f, aspectUV));
                    if (Mathf.Abs(corr - 1f) < 1e-3f) continue;

                    float sA = Mathf.Clamp(corr, scaleMin, scaleMax);
                    float sB = Mathf.Clamp(1f / corr, scaleMin, scaleMax);

                    Vector2 axis2 = new Vector2(-uvAxis1.y, uvAxis1.x);
                    foreach (int v in shell.vertexIndices)
                    {
                        int idx = v * 2;
                        if ((uint)(idx + 1) >= (uint)uvLen) continue;
                        float dx = uvFlat[idx] - uvCentroid.x;
                        float dy = uvFlat[idx + 1] - uvCentroid.y;
                        // Project onto principal axes, scale, project back.
                        float a1 = dx * uvAxis1.x + dy * uvAxis1.y;
                        float a2 = dx * axis2.x   + dy * axis2.y;
                        a1 *= sA;
                        a2 *= sB;
                        uvFlat[idx]     = uvCentroid.x + a1 * uvAxis1.x + a2 * axis2.x;
                        uvFlat[idx + 1] = uvCentroid.y + a1 * uvAxis1.y + a2 * axis2.y;
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

        // ── PCA helpers ──

        /// <summary>
        /// 2D PCA over the shell's UV0 vertex positions: centroid, primary
        /// principal axis (unit vector), and the two std-devs (sqrt of the
        /// eigenvalues). Returns false on degenerate input.
        /// </summary>
        static bool ComputeShellUvPca(UvShell shell, float[] uvFlat,
            out Vector2 centroid, out Vector2 axis1, out float sigma1, out float sigma2)
        {
            centroid = Vector2.zero;
            axis1 = new Vector2(1, 0);
            sigma1 = 0; sigma2 = 0;
            if (shell?.vertexIndices == null || shell.vertexIndices.Count == 0) return false;
            int uvLen = uvFlat.Length;

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
            double mx = sx / count, my = sy / count;
            centroid = new Vector2((float)mx, (float)my);

            double cxx = 0, cyy = 0, cxy = 0;
            foreach (int v in shell.vertexIndices)
            {
                int idx = v * 2;
                if ((uint)(idx + 1) >= (uint)uvLen) continue;
                double dx = uvFlat[idx] - mx;
                double dy = uvFlat[idx + 1] - my;
                cxx += dx * dx;
                cyy += dy * dy;
                cxy += dx * dy;
            }
            cxx /= count; cyy /= count; cxy /= count;

            double tr = cxx + cyy;
            double det = cxx * cyy - cxy * cxy;
            double disc = Math.Sqrt(Math.Max(0.0, tr * tr * 0.25 - det));
            double lambda1 = tr * 0.5 + disc;
            double lambda2 = tr * 0.5 - disc;
            if (lambda2 < 0) lambda2 = 0;

            // Primary eigenvector v1 satisfies (C - λ1 I) v1 = 0.
            // Choosing v1 = (cxy, λ1 - cxx) when cxy != 0 avoids the diagonal
            // singularity; fall back to axis-aligned when cov matrix is diagonal.
            if (Math.Abs(cxy) > 1e-14)
            {
                Vector2 v = new Vector2((float)cxy, (float)(lambda1 - cxx));
                if (v.sqrMagnitude < 1e-14f) v = new Vector2(1, 0);
                axis1 = v.normalized;
            }
            else
            {
                axis1 = (cxx >= cyy) ? new Vector2(1, 0) : new Vector2(0, 1);
            }
            sigma1 = (float)Math.Sqrt(lambda1);
            sigma2 = (float)Math.Sqrt(lambda2);
            return true;
        }

        /// <summary>
        /// 3D PCA constrained to the shell's mean tangent plane. Returns the
        /// std-devs along the two in-plane principal directions; the
        /// out-of-plane component (= surface thickness) is discarded. Approximate
        /// for highly curved surfaces but accurate for flat / near-flat shells.
        /// </summary>
        static bool ComputeShell3DInPlanePca(UvShell shell, int[] tris, Vector3[] positions,
            out float sigma1, out float sigma2)
        {
            sigma1 = 0; sigma2 = 0;
            if (shell?.faceIndices == null || shell.vertexIndices == null) return false;
            int posLen = positions.Length;

            // Area-weighted normal (so big faces dominate; avoids tiny-edge artefacts).
            Vector3 sumNormal = Vector3.zero;
            foreach (int f in shell.faceIndices)
            {
                int t = f * 3;
                if ((uint)(t + 2) >= (uint)tris.Length) continue;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                sumNormal += Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            }
            Vector3 normal = sumNormal.sqrMagnitude > 1e-12f ? sumNormal.normalized : Vector3.up;

            // Orthonormal basis on the plane.
            Vector3 helper = Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up;
            Vector3 e1 = Vector3.Cross(normal, helper).normalized;
            Vector3 e2 = Vector3.Cross(normal, e1).normalized;

            // Centroid in 3D, then build 2D in-plane points.
            Vector3 sumPos = Vector3.zero;
            int count = 0;
            foreach (int v in shell.vertexIndices)
            {
                if ((uint)v >= (uint)posLen) continue;
                sumPos += positions[v];
                count++;
            }
            if (count == 0) return false;
            Vector3 centroid3D = sumPos / count;

            double cxx = 0, cyy = 0, cxy = 0;
            foreach (int v in shell.vertexIndices)
            {
                if ((uint)v >= (uint)posLen) continue;
                Vector3 d = positions[v] - centroid3D;
                double a = Vector3.Dot(d, e1);
                double b = Vector3.Dot(d, e2);
                cxx += a * a;
                cyy += b * b;
                cxy += a * b;
            }
            cxx /= count; cyy /= count; cxy /= count;

            double tr = cxx + cyy;
            double det = cxx * cyy - cxy * cxy;
            double disc = Math.Sqrt(Math.Max(0.0, tr * tr * 0.25 - det));
            double lambda1 = tr * 0.5 + disc;
            double lambda2 = tr * 0.5 - disc;
            if (lambda2 < 0) lambda2 = 0;
            sigma1 = (float)Math.Sqrt(lambda1);
            sigma2 = (float)Math.Sqrt(lambda2);
            return true;
        }
    }
}
