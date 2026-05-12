// TexelDensityNormalizer.cs — pre-pack UV0 rescale so each shell's
// UV-area is proportional to its 3D surface area. Produces uniform
// texels-per-world-unit in the final lightmap UV2.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TexelDensityNormalizer
    {
        /// <summary>
        /// Per-shell rescale of UV0 (operates on the local <paramref name="uvFlat"/>
        /// copy fed to xatlas) so each shell's UV-area is proportional to its 3D
        /// surface area. After this pass xatlas packs shells with uniform texel
        /// density per world unit, and the baked lightmap has consistent detail
        /// across the model — small 3D parts get small atlas slots, big parts get
        /// big slots, regardless of how the source UV0 was authored.
        ///
        /// Method:
        ///   per-shell:    density_i  = areaUV0_i / area3D_i
        ///   global:       density_t  = Σ areaUV0 / Σ area3D    (preserves total UV area)
        ///   per-shell:    scale_i    = sqrt(density_t / density_i)
        ///   apply scale around each shell's UV-bbox centroid (preserves centroid,
        ///   alters only extent — so subsequent perturbation pivots stay valid).
        ///
        /// Does NOT modify the original mesh.uv — only the in-memory uvFlat that
        /// is handed to xatlas's AddUvMesh. The shells' cached boundsMin/boundsMax
        /// are also left untouched intentionally; downstream consumers that need
        /// post-normalize bbox should recompute from uvFlat.
        /// </summary>
        /// <param name="uvFlat">Flat UV0 array (vertexCount * 2 floats). Mutated in place.</param>
        /// <param name="shells">Per-mesh shells (read-only).</param>
        /// <param name="tris">Mesh triangles.</param>
        /// <param name="positions">Mesh vertex positions in mesh-local space.</param>
        /// <param name="scaleMin">Lower clamp on per-shell scale. Default 0.1 — without this
        /// a shell whose UV0 is already vastly oversized would collapse to a point.</param>
        /// <param name="scaleMax">Upper clamp on per-shell scale. Default 10 — without this
        /// a single tiny-UV shell could blow up to dominate the atlas and starve everyone else.</param>
        /// <param name="medianDensity">When true, use the median per-shell density as the
        /// target (robust against a few extreme outliers). When false, use the area-weighted
        /// global average — preserves total UV area exactly but is sensitive to outliers.
        /// Default false (area-weighted).</param>
        /// <returns>Number of shells whose UV0 was rescaled (excludes scale≈1 no-ops).</returns>
        internal static int Normalize(
            float[] uvFlat,
            List<UvShell> shells,
            int[] tris,
            Vector3[] positions,
            float scaleMin = 0.1f,
            float scaleMax = 10f,
            bool medianDensity = false)
        {
            if (uvFlat == null || shells == null || shells.Count == 0) return 0;
            if (tris == null || positions == null) return 0;
            if (scaleMin <= 0f) scaleMin = 0.1f;
            if (scaleMax < scaleMin) scaleMax = scaleMin;

            int n = shells.Count;
            var area3DPerShell = new double[n];
            var areaUVPerShell = new double[n];
            double sumArea3D = 0.0;
            double sumAreaUV = 0.0;

            int triPosLen = positions.Length;
            int uvLen = uvFlat.Length;

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
                    if ((uint)i0 >= (uint)triPosLen ||
                        (uint)i1 >= (uint)triPosLen ||
                        (uint)i2 >= (uint)triPosLen) continue;

                    Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                    Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                    a3 += cross.magnitude * 0.5;

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

            // Target density (UV area per 3D area). Two strategies:
            //   - area-weighted: Σ areaUV / Σ area3D  — preserves total UV area
            //   - median: ignores the long tail, robust against outliers
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

            int modified = 0;
            for (int si = 0; si < n; si++)
            {
                double a3 = area3DPerShell[si];
                double au = areaUVPerShell[si];
                if (a3 < 1e-12 || au < 1e-12) continue;

                // desired UV area = a3 * densityTarget
                // current UV area = au
                // linear scale factor = sqrt(desired / current)
                double desired = a3 * densityTarget;
                double scaleSq = desired / au;
                if (double.IsNaN(scaleSq) || double.IsInfinity(scaleSq) || scaleSq <= 0.0) continue;
                float scale = (float)Math.Sqrt(scaleSq);
                if (float.IsNaN(scale) || float.IsInfinity(scale)) continue;
                scale = Mathf.Clamp(scale, scaleMin, scaleMax);
                if (Mathf.Abs(scale - 1f) < 1e-4f) continue;

                var shell = shells[si];
                if (shell.vertexIndices == null || shell.vertexIndices.Count == 0) continue;

                Vector2 c = (shell.boundsMin + shell.boundsMax) * 0.5f;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    float u = uvFlat[idx];
                    float w = uvFlat[idx + 1];
                    uvFlat[idx]     = c.x + (u - c.x) * scale;
                    uvFlat[idx + 1] = c.y + (w - c.y) * scale;
                }
                modified++;
            }

            return modified;
        }
    }
}
