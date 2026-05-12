// TexelDensityNormalizer.cs — pre-pack UV0 reshaping in three passes:
//   1) Global unwrap-aspect normalization. Detect the AABB of all UV0 vertices
//      across the mesh. If the artist authored on a non-square atlas (e.g.
//      1:2 or 1:0.5 unwrap), apply a single non-uniform global scale around
//      the bbox centre to make the overall span 1:1. Area-preserving by
//      construction (sx·sy = 1).
//   1.5) (Opt-in) Per-shell aspect normalization. For each shell, take a 3D
//      PCA in the shell's tangent plane (σ1≥σ2 → 3D aspect = √(σ1/σ2)),
//      align the UV principal axis with the 3D principal axis via a 2×2 SVD
//      between UV offsets and tangent-plane offsets, then area-preserving
//      non-uniformly scale so UV bbox aspect matches the (clamped) 3D PCA
//      aspect. Reduces UV slivers caused by elongated unwraps.
//   2) Texel density correction (uniform per-shell scale) bringing UV-area
//      proportional to 3D-area, plus a global coverage budget that leaves
//      slack for xatlas's bin-packing.
//
// All passes mutate the local uvFlat copy fed to xatlas. mesh.uv is never
// touched (project invariant).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TexelDensityNormalizer
    {
        /// <summary>
        /// Three-pass per-shell UV0 reshape: (1) global unwrap-aspect to 1:1,
        /// (1.5, opt-in) per-shell aspect to match 3D PCA aspect,
        /// (2) area-based density correction. Earlier passes run first so the
        /// later passes measure UV area / shape on the reshaped layout.
        /// </summary>
        /// <param name="uvFlat">Flat UV0 array (vertexCount * 2 floats). Mutated in place.</param>
        /// <param name="shells">Per-mesh shells (read-only).</param>
        /// <param name="tris">Mesh triangles (currently unused in pass 1 but kept for the density pass).</param>
        /// <param name="positions">Mesh vertex positions in mesh-local space.</param>
        /// <param name="scaleMin">Safety clamp on any single-axis scale. Default 0.1.</param>
        /// <param name="scaleMax">Safety clamp on any single-axis scale. Default 10.</param>
        /// <param name="medianDensity">When true the density target is the median per-shell density (robust
        /// against outliers); when false it's the area-weighted average. Default false.</param>
        /// <param name="targetCoverage">After per-shell density normalisation, total UV area is rescaled
        /// to this fraction of [0,1]² so xatlas doesn't overflow the requested atlas resolution due to
        /// bin-packing slack. Default 0.75. ≤0 or ≥1 disables the budget step.</param>
        /// <param name="normalizeAspect">Enable pass 1 (global unwrap-aspect to 1:1). Default true.</param>
        /// <param name="maxAspect">Upper bound on the per-shell 3D-PCA aspect used by the
        /// per-shell aspect pass. The shell's √(σ1/σ2) is clamped to [1, maxAspect] before being
        /// used as the UV aspect target. Default 10.</param>
        /// <param name="perShellAspect">Enable pass 1.5 (per-shell aspect → 3D PCA aspect). Default false.</param>
        /// <returns>1 if any pre-density pass modified UVs, plus the count of shells modified by pass 2.</returns>
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
            float maxAspect = 10f,
            bool perShellAspect = false)
        {
            if (uvFlat == null || shells == null || shells.Count == 0) return 0;
            if (tris == null || positions == null) return 0;
            if (scaleMin <= 0f) scaleMin = 0.1f;
            if (scaleMax < scaleMin) scaleMax = scaleMin;

            int n = shells.Count;
            int uvLen = uvFlat.Length;
            int posLen = positions.Length;
            int modifiedDensity = 0;
            bool aspectModified = false;

            // ── PASS 1: global unwrap-aspect → 1:1 ──
            // The artist may have authored the UV0 layout on a non-square
            // atlas (1×2, 1×0.5, …). Such non-uniform global scaling burns
            // anisotropic texel density into every shell. Detect the overall
            // UV0 bbox aspect and apply a single area-preserving non-uniform
            // global scale around the bbox centre so the new bbox is square.
            //
            // Only the *global* aspect is corrected. Per-shell aspect issues
            // (mismatch between an individual shell's UV0 shape and its 3D
            // shape) are intentionally left for a separate stage — they
            // require per-shell measurement (PCA / Sander metric / OBB) and
            // can break some inputs, so they belong in their own opt-in pass.
            if (normalizeAspect)
            {
                Vector2 gMin = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 gMax = new Vector2(float.MinValue, float.MinValue);
                bool hasAny = false;
                for (int si = 0; si < n; si++)
                {
                    var shell = shells[si];
                    if (shell?.vertexIndices == null) continue;
                    foreach (int v in shell.vertexIndices)
                    {
                        int idx = v * 2;
                        if ((uint)(idx + 1) >= (uint)uvLen) continue;
                        float ux = uvFlat[idx];
                        float uy = uvFlat[idx + 1];
                        if (ux < gMin.x) gMin.x = ux;
                        if (uy < gMin.y) gMin.y = uy;
                        if (ux > gMax.x) gMax.x = ux;
                        if (uy > gMax.y) gMax.y = uy;
                        hasAny = true;
                    }
                }

                if (hasAny)
                {
                    float w = gMax.x - gMin.x;
                    float h = gMax.y - gMin.y;
                    if (w > 1e-6f && h > 1e-6f)
                    {
                        float aspect = w / h;
                        if (Mathf.Abs(aspect - 1f) > 1e-3f)
                        {
                            // Area-preserving non-uniform scale: sx·sy = 1
                            // and sx·w = sy·h  →  sx = sqrt(h/w), sy = sqrt(w/h).
                            float sx = Mathf.Sqrt(h / w);
                            float sy = Mathf.Sqrt(w / h);
                            sx = Mathf.Clamp(sx, scaleMin, scaleMax);
                            sy = Mathf.Clamp(sy, scaleMin, scaleMax);

                            Vector2 c = (gMin + gMax) * 0.5f;
                            for (int i = 0; i < uvFlat.Length - 1; i += 2)
                            {
                                uvFlat[i]     = c.x + (uvFlat[i]     - c.x) * sx;
                                uvFlat[i + 1] = c.y + (uvFlat[i + 1] - c.y) * sy;
                            }
                            UvtLog.Verbose(UvtLog.Category.Repack,
                                $"Global unwrap aspect: bbox {w:F3}x{h:F3} " +
                                $"(aspect {aspect:F2}:1), scaled by ({sx:F3},{sy:F3}) → 1:1");
                            aspectModified = true;
                        }
                    }
                }
            }

            // ── PASS 1.5: per-shell aspect → 3D PCA aspect (opt-in) ──
            // For each shell, take a 3D PCA in its tangent plane (top-2
            // eigenvectors of the 3×3 position covariance). Align the UV
            // principal axis with the 3D principal axis via a 2×2 SVD
            // between UV offsets and tangent-plane offsets, then apply an
            // area-preserving non-uniform scale so the UV bbox aspect (in
            // the aligned frame) equals √(σ1/σ2), clamped to [1, maxAspect].
            // Rotates back, leaving the shell's centroid in place.
            if (perShellAspect)
                ApplyPerShellAspect(uvFlat, shells, positions, scaleMin, scaleMax, maxAspect,
                    ref aspectModified);

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

            if (sumArea3D < 1e-12 || sumAreaUV < 1e-12) return aspectModified ? 1 : 0;

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
                if (densities.Count == 0) return aspectModified ? 1 : 0;
                densities.Sort();
                densityTarget = densities[densities.Count / 2];
            }
            else
            {
                densityTarget = sumAreaUV / sumArea3D;
            }
            if (densityTarget < 1e-12) return aspectModified ? 1 : 0;

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

            return Mathf.Max(aspectModified ? 1 : 0, modifiedDensity);
        }

        // ────────────────────────────────────────────────────────────────
        // PASS 1.5 helpers — per-shell aspect → 3D PCA aspect
        // ────────────────────────────────────────────────────────────────

        // Skip-condition tuning. Lifted to consts for grep-ability.
        private const float kPerShellMinSigma2     = 1e-10f; // absolute σ2 floor
        private const float kPerShellMinSigmaRatio = 1e-6f;  // σ2/σ1 floor (vs degenerate line/point shells)
        private const float kPerShellTrivialEps    = 1e-3f;  // |a3D − aUV| under which we skip
        private const float kPerShellTangentEps    = 1e-12f; // 3D tangent-plane offset sumSq floor
        private const float kPerShellUvEps         = 1e-12f; // UV offset sumSq floor

        /// <summary>
        /// Per-shell aspect normalization. For each shell, computes the 3D
        /// PCA tangent plane (two top eigenvectors of the 3×3 position
        /// covariance), aligns the UV principal axis to the 3D principal
        /// axis via a 2×2 SVD between the centered UV offsets and the
        /// tangent-plane offsets, then applies an area-preserving non-uniform
        /// scale around the UV centroid so the UV bbox aspect in the aligned
        /// frame equals √(σ1/σ2) clamped to [1, maxAspect]. Finally rotates
        /// the shell back. Centroid is preserved.
        /// </summary>
        private static void ApplyPerShellAspect(
            float[] uvFlat,
            List<UvShell> shells,
            Vector3[] positions,
            float scaleMin,
            float scaleMax,
            float maxAspect,
            ref bool anyModified)
        {
            int n = shells.Count;
            int uvLen = uvFlat.Length;

            int modified = 0;
            int skipDegenerate = 0;
            int skipTrivial = 0;
            int skipNumeric = 0;
            int skipClamp = 0;

            float maxAspectClamp = maxAspect > 1f ? maxAspect : 1f;

            for (int si = 0; si < n; si++)
            {
                var shell = shells[si];
                if (shell?.vertexIndices == null || shell.vertexIndices.Count < 3)
                {
                    skipDegenerate++;
                    continue;
                }

                // 3D PCA — top-2 eigenvectors of position covariance.
                if (!Compute3DShellAxes(shell, positions,
                        out double sigma1, out double sigma2,
                        out Vector3 e1_3D, out Vector3 e2_3D,
                        out Vector3 c3D))
                {
                    skipDegenerate++;
                    continue;
                }

                if (sigma2 < kPerShellMinSigma2 || sigma1 <= 0.0 ||
                    sigma2 / sigma1 < kPerShellMinSigmaRatio)
                {
                    skipDegenerate++;
                    continue;
                }

                double aspect3D_raw = Math.Sqrt(sigma1 / sigma2);
                if (double.IsNaN(aspect3D_raw) || double.IsInfinity(aspect3D_raw))
                {
                    skipNumeric++;
                    continue;
                }
                float aspect3D = Mathf.Clamp((float)aspect3D_raw, 1f, maxAspectClamp);

                // UV centroid + paired tangent-plane / UV offset accumulators
                // for the 2×2 cross-covariance.
                Vector2 cUv = Vector2.zero;
                int cn = 0;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    cUv.x += uvFlat[idx];
                    cUv.y += uvFlat[idx + 1];
                    cn++;
                }
                if (cn < 3)
                {
                    skipDegenerate++;
                    continue;
                }
                cUv.x /= cn;
                cUv.y /= cn;

                // M = Σ [uOff; vOff] · [t1, t2]
                double m00 = 0.0, m01 = 0.0, m10 = 0.0, m11 = 0.0;
                double tSumSq = 0.0, uSumSq = 0.0;
                int posLen = positions.Length;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    if ((uint)v >= (uint)posLen) continue;

                    Vector3 d3 = positions[v] - c3D;
                    double t1 = Vector3.Dot(d3, e1_3D);
                    double t2 = Vector3.Dot(d3, e2_3D);

                    double uOff = uvFlat[idx]     - cUv.x;
                    double vOff = uvFlat[idx + 1] - cUv.y;

                    m00 += uOff * t1;
                    m01 += uOff * t2;
                    m10 += vOff * t1;
                    m11 += vOff * t2;

                    tSumSq += t1 * t1 + t2 * t2;
                    uSumSq += uOff * uOff + vOff * vOff;
                }

                if (tSumSq < kPerShellTangentEps || uSumSq < kPerShellUvEps)
                {
                    skipDegenerate++;
                    continue;
                }

                // 2×2 SVD → rotation angle θ that aligns UV principal axis
                // with 3D principal axis. R = U·V^T. We only need θ.
                if (!Compute2DRotationAngle(m00, m01, m10, m11, out double theta))
                {
                    skipNumeric++;
                    continue;
                }

                // Rotate UV offsets by −θ around cUv → axis-align with 3D PCA.
                float cosT = (float)Math.Cos(-theta);
                float sinT = (float)Math.Sin(-theta);

                // First pass: measure aligned bbox.
                float minA = float.MaxValue, minB = float.MaxValue;
                float maxA = float.MinValue, maxB = float.MinValue;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    float du = uvFlat[idx]     - cUv.x;
                    float dv = uvFlat[idx + 1] - cUv.y;
                    float a = du * cosT - dv * sinT;
                    float b = du * sinT + dv * cosT;
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                    if (b < minB) minB = b;
                    if (b > maxB) maxB = b;
                }

                float wA = maxA - minA;
                float hB = maxB - minB;
                if (wA < 1e-6f || hB < 1e-6f)
                {
                    skipDegenerate++;
                    continue;
                }

                // If the swap protector fires the SVD result was off — flip
                // axes by adding π/2 to θ. This re-derives a, b on the fly
                // below; cheaper to just recompute cos/sin and bbox.
                if (wA < hB)
                {
                    theta += Math.PI * 0.5;
                    cosT = (float)Math.Cos(-theta);
                    sinT = (float)Math.Sin(-theta);
                    minA = float.MaxValue; minB = float.MaxValue;
                    maxA = float.MinValue; maxB = float.MinValue;
                    foreach (int v in shell.vertexIndices)
                    {
                        int idx = v * 2;
                        if ((uint)(idx + 1) >= (uint)uvLen) continue;
                        float du = uvFlat[idx]     - cUv.x;
                        float dv = uvFlat[idx + 1] - cUv.y;
                        float a = du * cosT - dv * sinT;
                        float b = du * sinT + dv * cosT;
                        if (a < minA) minA = a;
                        if (a > maxA) maxA = a;
                        if (b < minB) minB = b;
                        if (b > maxB) maxB = b;
                    }
                    wA = maxA - minA;
                    hB = maxB - minB;
                    if (wA < 1e-6f || hB < 1e-6f)
                    {
                        skipDegenerate++;
                        continue;
                    }
                }

                float aspectUV = wA / hB;

                if (Mathf.Abs(aspect3D - aspectUV) < kPerShellTrivialEps)
                {
                    skipTrivial++;
                    continue;
                }

                // Area-preserving non-uniform scale: along A (principal) by
                // sx = √(aspect3D / aspectUV), perpendicular by sy = 1/sx.
                float sx = Mathf.Sqrt(aspect3D / aspectUV);
                float sy = aspectUV > 0f ? Mathf.Sqrt(aspectUV / aspect3D) : 0f;
                if (float.IsNaN(sx) || float.IsInfinity(sx) ||
                    float.IsNaN(sy) || float.IsInfinity(sy) ||
                    sx <= 0f || sy <= 0f)
                {
                    skipNumeric++;
                    continue;
                }
                if (sx < scaleMin || sx > scaleMax || sy < scaleMin || sy > scaleMax)
                {
                    skipClamp++;
                    continue;
                }

                // Compose: rotate −θ, scale (sx,sy), rotate +θ, around cUv.
                // Precompute the 2×2 matrix R(+θ) · diag(sx,sy) · R(−θ).
                float cosP = (float)Math.Cos(theta);
                float sinP = (float)Math.Sin(theta);
                // After the algebra:
                //   M[0,0] =  cosP*sx*cosT + sinP*sy*sinT  (note R(−θ) entries)
                // Compute via R(+θ) * diag(sx,sy) * R(−θ) explicitly.
                // R(−θ) = [[cosT, -sinT],[sinT, cosT]]
                // diag·R(−θ) = [[sx*cosT, -sx*sinT],[sy*sinT, sy*cosT]]
                // R(+θ) * that:
                //   row0 = cosP*[sx*cosT,-sx*sinT] - sinP*[sy*sinT, sy*cosT]
                //        = [cosP*sx*cosT - sinP*sy*sinT, -cosP*sx*sinT - sinP*sy*cosT]
                //   row1 = sinP*[sx*cosT,-sx*sinT] + cosP*[sy*sinT, sy*cosT]
                //        = [sinP*sx*cosT + cosP*sy*sinT, -sinP*sx*sinT + cosP*sy*cosT]
                float a00 =  cosP * sx * cosT - sinP * sy * sinT;
                float a01 = -cosP * sx * sinT - sinP * sy * cosT;
                float a10 =  sinP * sx * cosT + cosP * sy * sinT;
                float a11 = -sinP * sx * sinT + cosP * sy * cosT;

                bool numericFail = false;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    float du = uvFlat[idx]     - cUv.x;
                    float dv = uvFlat[idx + 1] - cUv.y;
                    float nu = cUv.x + a00 * du + a01 * dv;
                    float nv = cUv.y + a10 * du + a11 * dv;
                    if (float.IsNaN(nu) || float.IsInfinity(nu) ||
                        float.IsNaN(nv) || float.IsInfinity(nv))
                    {
                        numericFail = true;
                        break;
                    }
                    uvFlat[idx]     = nu;
                    uvFlat[idx + 1] = nv;
                }

                if (numericFail)
                {
                    skipNumeric++;
                    continue;
                }

                modified++;
                UvtLog.Verbose(UvtLog.Category.Repack,
                    $"[Repack] PerShellAspect: shell {si} 3D {aspect3D:F2}:1 UV {aspectUV:F2}:1 " +
                    $"θ={theta * (180.0 / Math.PI):F1}° scale=({sx:F3},{sy:F3})");
            }

            if (modified > 0) anyModified = true;

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Repack] PerShellAspect: normalized {modified}/{n} shells " +
                $"(skipped {skipDegenerate} degenerate, {skipTrivial} already normalized, " +
                $"{skipNumeric} numeric, {skipClamp} out-of-scale-range)");
        }

        /// <summary>
        /// Power-iteration + deflation on the 3×3 position covariance of the
        /// shell. Returns the top-2 eigenvalues (σ1≥σ2) and eigenvectors as
        /// the shell's tangent plane, plus the 3D centroid. Returns false if
        /// the shell has too few vertices or the covariance is degenerate.
        /// </summary>
        private static bool Compute3DShellAxes(
            UvShell shell, Vector3[] positions,
            out double sigma1, out double sigma2,
            out Vector3 e1, out Vector3 e2,
            out Vector3 centroid)
        {
            sigma1 = 0.0; sigma2 = 0.0;
            e1 = Vector3.right; e2 = Vector3.up;
            centroid = Vector3.zero;

            int posLen = positions.Length;
            int count = 0;
            Vector3 sum = Vector3.zero;
            foreach (int v in shell.vertexIndices)
            {
                if ((uint)v >= (uint)posLen) continue;
                sum += positions[v];
                count++;
            }
            if (count < 3) return false;
            centroid = sum / count;

            double cxx = 0, cxy = 0, cxz = 0;
            double cyy = 0, cyz = 0, czz = 0;
            foreach (int v in shell.vertexIndices)
            {
                if ((uint)v >= (uint)posLen) continue;
                double dx = positions[v].x - centroid.x;
                double dy = positions[v].y - centroid.y;
                double dz = positions[v].z - centroid.z;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz;
                czz += dz * dz;
            }
            double inv = 1.0 / count;
            cxx *= inv; cxy *= inv; cxz *= inv;
            cyy *= inv; cyz *= inv; czz *= inv;

            // Power iteration for largest eigen-pair.
            Vector3 v1 = new Vector3(1f, 0.5f, 0.25f).normalized;
            for (int iter = 0; iter < 64; iter++)
            {
                double nx = cxx * v1.x + cxy * v1.y + cxz * v1.z;
                double ny = cxy * v1.x + cyy * v1.y + cyz * v1.z;
                double nz = cxz * v1.x + cyz * v1.y + czz * v1.z;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-14) return false;
                v1 = new Vector3((float)(nx / len), (float)(ny / len), (float)(nz / len));
            }
            // Eigenvalue (Rayleigh quotient).
            double a1x = cxx * v1.x + cxy * v1.y + cxz * v1.z;
            double a1y = cxy * v1.x + cyy * v1.y + cyz * v1.z;
            double a1z = cxz * v1.x + cyz * v1.y + czz * v1.z;
            sigma1 = v1.x * a1x + v1.y * a1y + v1.z * a1z;
            if (sigma1 <= 0.0) return false;

            // Deflate and iterate for second eigen-pair.
            double dxx = cxx - sigma1 * v1.x * v1.x;
            double dxy = cxy - sigma1 * v1.x * v1.y;
            double dxz = cxz - sigma1 * v1.x * v1.z;
            double dyy = cyy - sigma1 * v1.y * v1.y;
            double dyz = cyz - sigma1 * v1.y * v1.z;
            double dzz = czz - sigma1 * v1.z * v1.z;

            // Seed orthogonal to v1.
            Vector3 seed = Mathf.Abs(Vector3.Dot(v1, Vector3.up)) < 0.95f
                ? Vector3.Cross(v1, Vector3.up).normalized
                : Vector3.Cross(v1, Vector3.right).normalized;
            Vector3 v2 = seed;
            for (int iter = 0; iter < 64; iter++)
            {
                double nx = dxx * v2.x + dxy * v2.y + dxz * v2.z;
                double ny = dxy * v2.x + dyy * v2.y + dyz * v2.z;
                double nz = dxz * v2.x + dyz * v2.y + dzz * v2.z;
                // Re-orthogonalize against v1 (numerical hygiene).
                double dot = v1.x * nx + v1.y * ny + v1.z * nz;
                nx -= dot * v1.x; ny -= dot * v1.y; nz -= dot * v1.z;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-14) return false;
                v2 = new Vector3((float)(nx / len), (float)(ny / len), (float)(nz / len));
            }
            double a2x = cxx * v2.x + cxy * v2.y + cxz * v2.z;
            double a2y = cxy * v2.x + cyy * v2.y + cyz * v2.z;
            double a2z = cxz * v2.x + cyz * v2.y + czz * v2.z;
            sigma2 = v2.x * a2x + v2.y * a2y + v2.z * a2z;
            if (sigma2 <= 0.0) return false;

            e1 = v1;
            e2 = v2;
            return true;
        }

        /// <summary>
        /// 2×2 SVD reduced to the rotation angle θ such that U·V^T = R(θ) =
        /// [[cosθ,-sinθ],[sinθ,cosθ]], where M = U·Σ·V^T. Derivation:
        ///   • S = M^T·M is 2×2 symmetric; its eigenvectors form V's columns.
        ///   • The principal eigenvector of S (largest eigenvalue) gives the
        ///     direction in the *input* space (= 3D-tangent-plane axes here)
        ///     that M maps to the largest principal direction in the output
        ///     space (= UV plane).
        ///   • U[:,0] = M·V[:,0] / s1 (largest singular value).
        ///   • R = U·V^T rotates V's first column onto U's first column —
        ///     i.e. rotates the 3D-aligned-frame's principal axis into the
        ///     UV-frame's principal axis. We want the *inverse*: rotate UV
        ///     to be aligned with 3D-PCA, so the caller applies R(−θ).
        /// Reflection (det(R) &lt; 0) is folded out by flipping V's second
        /// column, yielding a pure rotation; θ is unaffected.
        /// </summary>
        private static bool Compute2DRotationAngle(
            double m00, double m01, double m10, double m11, out double theta)
        {
            theta = 0.0;
            if (Math.Abs(m00) + Math.Abs(m01) + Math.Abs(m10) + Math.Abs(m11) < 1e-20)
                return false;

            // S = M^T M, 2×2 symmetric.
            //   S[0,0] = m00² + m10²
            //   S[1,1] = m01² + m11²
            //   S[0,1] = S[1,0] = m00*m01 + m10*m11
            double s00 = m00 * m00 + m10 * m10;
            double s11 = m01 * m01 + m11 * m11;
            double s01 = m00 * m01 + m10 * m11;

            // Eigen-angle φ of S: tan(2φ) = 2·s01 / (s00 − s11). The
            // principal eigenvector V[:,0] = (cos φ, sin φ).
            double phi = 0.5 * Math.Atan2(2.0 * s01, s00 - s11);
            double cosPhi = Math.Cos(phi);
            double sinPhi = Math.Sin(phi);

            // u = M · V[:,0]  (un-normalized first column of U·Σ).
            double ux = m00 * cosPhi + m01 * sinPhi;
            double uy = m10 * cosPhi + m11 * sinPhi;
            double s1 = Math.Sqrt(ux * ux + uy * uy);
            if (s1 < 1e-15) return false;

            // U[:,0] = (cos ψ, sin ψ).
            double psi = Math.Atan2(uy, ux);

            // R = U·V^T. U = R(ψ) · diag(±1,±1) absorbing reflection into V
            // (no effect on rotation). So R = R(ψ − φ).
            theta = psi - phi;
            return !double.IsNaN(theta) && !double.IsInfinity(theta);
        }
    }
}
