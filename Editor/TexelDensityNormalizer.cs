// TexelDensityNormalizer.cs — pre-pack UV0 density correction.
//
// Single pass: per-shell uniform scale so each shell's UV-area is proportional
// to its 3D surface area, modulated by a global coverage budget that leaves
// slack for xatlas's bin-packing.
//
// Mutates the local uvFlat copy fed to xatlas. mesh.uv is never touched
// (project invariant).
//
// History: an earlier global non-uniform "unwrap aspect" pass scaled the
// overall UV0 bbox to 1:1 before this density pass. That pass was removed —
// xatlas does not require a 1:1 input UV0, and ARAP per-shell parameterization
// (when enabled) handles distortion at the correct level. Global anisotropic
// scale only ever fought ARAP's output. See repo history for the deletion
// rationale.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TexelDensityNormalizer
    {
        /// <summary>
        /// Per-shell uniform-scale density correction. Each shell is rescaled
        /// around its UV centroid so that UV-area / 3D-area is constant across
        /// shells, optionally clamped to a coverage budget.
        /// </summary>
        /// <param name="uvFlat">Flat UV0 array (vertexCount * 2 floats). Mutated in place.</param>
        /// <param name="shells">Per-mesh shells (read-only).</param>
        /// <param name="tris">Mesh triangle index buffer.</param>
        /// <param name="positions">Mesh vertex positions in mesh-local space.</param>
        /// <param name="scaleMin">Safety clamp on the per-shell scale. Default 0.1.</param>
        /// <param name="scaleMax">Safety clamp on the per-shell scale. Default 10.</param>
        /// <param name="medianDensity">When true the density target is the median per-shell density (robust
        /// against outliers); when false it's the area-weighted average. Default false.</param>
        /// <param name="targetCoverage">After per-shell density normalisation, total UV area is rescaled
        /// to this fraction of [0,1]² so xatlas doesn't overflow the requested atlas resolution due to
        /// bin-packing slack. Default 0.75. ≤0 or ≥1 disables the budget step.</param>
        /// <returns>Number of shells modified by the density pass.</returns>
        internal static int Normalize(
            float[] uvFlat,
            List<UvShell> shells,
            int[] tris,
            Vector3[] positions,
            float scaleMin = 0.1f,
            float scaleMax = 10f,
            bool medianDensity = false,
            float targetCoverage = 0.95f)
        {
            if (uvFlat == null || shells == null || shells.Count == 0) return 0;
            if (tris == null || positions == null) return 0;
            if (scaleMin <= 0f) scaleMin = 0.1f;
            if (scaleMax < scaleMin) scaleMax = scaleMin;

            int n = shells.Count;
            int uvLen = uvFlat.Length;
            int posLen = positions.Length;
            int modifiedDensity = 0;

            // ── Density correction ──
            // Measure UV area + 3D area per shell, then apply per-shell uniform
            // scale so UV-area / 3D-area is constant across all shells,
            // modulated by the coverage budget.
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

            if (sumArea3D < 1e-12 || sumAreaUV < 1e-12) return 0;

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
                if (densities.Count == 0) return 0;
                densities.Sort();
                densityTarget = densities[densities.Count / 2];
            }
            else
            {
                densityTarget = sumAreaUV / sumArea3D;
            }
            if (densityTarget < 1e-12) return 0;

            // Coverage budget: scale density target so total post-normalize UV
            // area equals targetCoverage fraction of [0,1]².
            if (targetCoverage > 0f && targetCoverage < 1f && sumArea3D > 1e-12)
                densityTarget = targetCoverage / sumArea3D;

            // ── Diagnostics: pre-normalize density distribution ──
            // Density (au/a3) ratio across shells before normalisation. A
            // wide spread means the artist UV0 had uneven density and the
            // density pass has meaningful work to do; a narrow spread means
            // the input was already near-uniform and the pass will look
            // visually like "only a global scale was applied".
            double preMin = double.MaxValue, preMax = 0.0, preSum = 0.0;
            int preCount = 0;
            for (int si = 0; si < n; si++)
            {
                double a3 = area3DPerShell[si];
                double au = areaUVPerShell[si];
                if (a3 < 1e-12 || au < 1e-12) continue;
                double d = au / a3;
                if (d < preMin) preMin = d;
                if (d > preMax) preMax = d;
                preSum += d;
                preCount++;
            }
            double preMean = preCount > 0 ? preSum / preCount : 0.0;
            double preRatio = (preMin > 1e-30 && preMax > 0.0) ? preMax / preMin : 0.0;

            // Scale-distribution + post-normalize density tracking. We log a
            // summary so the user can verify the pass actually did per-shell
            // work and didn't collapse into a single global scale.
            double scaleMinSeen = double.MaxValue;
            double scaleMaxSeen = 0.0;
            double postMin = double.MaxValue, postMax = 0.0, postSum = 0.0;
            int postCount = 0;

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

                // Track distribution before the early-skip (so a uniform-input
                // run reports scale≈1 spread, proving it was a no-op rather
                // than silently skipping with no signal).
                if (scale < scaleMinSeen) scaleMinSeen = scale;
                if (scale > scaleMaxSeen) scaleMaxSeen = scale;

                // Post-normalize density = (au * scale²) / a3 = desired / a3 = densityTarget.
                // We re-derive from scale to detect clamp distortion.
                double postDensity = (au * (double)scale * (double)scale) / a3;
                if (postDensity < postMin) postMin = postDensity;
                if (postDensity > postMax) postMax = postDensity;
                postSum += postDensity;
                postCount++;

                if (Mathf.Abs(scale - 1f) < 1e-4f) continue;

                var shell = shells[si];
                if (shell.vertexIndices == null || shell.vertexIndices.Count == 0) continue;

                // Uniform scale around the shell's UV centroid keeps the
                // centroid fixed (so the layout doesn't drift) and doesn't
                // distort the shape — ARAP's per-shell parameterization, if
                // it ran, is preserved.
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

            double postMean = postCount > 0 ? postSum / postCount : 0.0;
            double postRatio = (postMin > 1e-30 && postMax > 0.0) ? postMax / postMin : 0.0;
            double scaleSpread = (scaleMinSeen < double.MaxValue && scaleMinSeen > 1e-30)
                ? scaleMaxSeen / scaleMinSeen : 1.0;

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Density] {modifiedDensity}/{n} shells rescaled, target={densityTarget:G3} | " +
                $"pre au/a3: min={preMin:G3} max={preMax:G3} mean={preMean:G3} maxRatio={preRatio:F2}x | " +
                $"post au/a3: min={postMin:G3} max={postMax:G3} mean={postMean:G3} maxRatio={postRatio:F2}x | " +
                $"scale: min={scaleMinSeen:F3} max={scaleMaxSeen:F3} spread={scaleSpread:F2}x");

            return modifiedDensity;
        }
    }
}
