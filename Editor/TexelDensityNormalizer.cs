// TexelDensityNormalizer.cs — pre-pack UV0 reshaping in two passes:
//   1) Global unwrap-aspect normalization. Detect the AABB of all UV0 vertices
//      across the mesh. If the artist authored on a non-square atlas (e.g.
//      1:2 or 1:0.5 unwrap), apply a single non-uniform global scale around
//      the bbox centre to make the overall span 1:1. Area-preserving by
//      construction (sx·sy = 1).
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
        /// Two-pass per-shell UV0 reshape: (1) global unwrap-aspect to 1:1,
        /// (2) area-based density correction. The earlier pass runs first so
        /// the density pass measures UV area on the reshaped layout.
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
        /// <returns>1 if the global-aspect pass modified UVs, plus the count of shells modified by the density pass.</returns>
        internal static int Normalize(
            float[] uvFlat,
            List<UvShell> shells,
            int[] tris,
            Vector3[] positions,
            float scaleMin = 0.1f,
            float scaleMax = 10f,
            bool medianDensity = false,
            float targetCoverage = 0.75f,
            bool normalizeAspect = true)
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
            // Only the *global* aspect is corrected. Per-shell parameterization
            // quality is handled separately via Sander L² + auto-ARAP in
            // XatlasRepack (see opts.reparameterizeStretchedShells). Affine
            // per-shell aspect hacks cannot fix bad parameterization — they
            // preserve relative triangle proportions and only move the bbox.
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
    }
}
