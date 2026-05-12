// TexelDensityNormalizer.cs — pre-pack UV0 reshaping in three passes:
//   1) Global unwrap-aspect normalization. Detect the AABB of all UV0 vertices
//      across the mesh. If the artist authored on a non-square atlas (e.g.
//      1:2 or 1:0.5 unwrap), apply a single non-uniform global scale around
//      the bbox centre to make the overall span 1:1. Area-preserving by
//      construction (sx·sy = 1).
//   1.5) (Opt-in) Per-shell aspect normalization. For each shell, compute
//      the *surface* aspect by edge-based BFS unfold of the triangle mesh
//      into 2D (developable approximation: hinges each new triangle across a
//      shared edge, flipped to the opposite half-plane from the parent's
//      third vertex). The unfold's 2D bbox aspect drives the UV target.
//      Then filter shells via Sander L² CV — if UV0 already preserves area
//      ratios well (cv < 0.5) we leave them alone, since they're the
//      artist's good unwraps. Otherwise align the UV principal axis to the
//      unfold's principal axis via a 2×2 SVD between UV offsets and unfold
//      offsets, and apply an area-preserving non-uniform scale to match the
//      (clamped) surface aspect. PCA on 3D positions is **deliberately
//      avoided** — it underestimates aspect for curved strips (a U-shaped
//      beam has σ1≈σ2≈σ3 even when the surface is a 20:1 ribbon).
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
        /// <param name="maxAspect">Upper bound on the per-shell surface aspect used by the
        /// per-shell aspect pass. The shell's unfold aspect is clamped to [1, maxAspect] before being
        /// used as the UV aspect target. Default 10.</param>
        /// <param name="perShellAspect">Enable pass 1.5 (per-shell aspect → surface unfold aspect). Default false.</param>
        /// <param name="perShellAspectThreshold">Relative skip threshold for the per-shell aspect pass:
        /// shells with |aspectSurface − aspectUV| / max(aspectSurface, aspectUV) below this value are
        /// left alone. Default 0.001 (effectively "fix anything that differs"). Larger → only fix
        /// grossly anisotropic shells.</param>
        /// <param name="perShellAspectSkipShells">Optional set of shell indices that should be excluded from the per-shell
        /// aspect pass. Used by the caller to hand off ribbon-classified shells to ARAP reparameterization instead
        /// (running both on the same ribbon collapses it). Null = no skips.</param>
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
            bool perShellAspect = false,
            float perShellAspectThreshold = 0.001f,
            int[] perShellAspectSkipShells = null)
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

            // ── PASS 1.5: per-shell aspect → surface unfold aspect (opt-in) ──
            // For each shell, unfold the triangle mesh edge-by-edge into 2D
            // (BFS, hinging across shared edges with the opposite-half-plane
            // rule). The 2D bbox aspect of the unfold is the surface aspect
            // target. Skip when Sander L² CV indicates the artist's UV0 is
            // already well proportioned. When applied, align UV principal
            // axis to the unfold principal axis via 2×2 SVD and apply an
            // area-preserving non-uniform scale; rotates back, centroid
            // preserved.
            if (perShellAspect)
                ApplyPerShellAspect(uvFlat, shells, tris, positions, scaleMin, scaleMax, maxAspect,
                    perShellAspectThreshold, perShellAspectSkipShells, ref aspectModified);

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
        // PASS 1.5 helpers — per-shell aspect → surface unfold aspect
        // ────────────────────────────────────────────────────────────────

        // Skip-condition tuning. Lifted to consts for grep-ability.
        private const int   kPerShellMinTriangles    = 3;     // shells with fewer triangles are trivial
        private const float kPerShellMinAspectRatio  = 1e-6f; // surface aspect denom floor
        private const float kPerShellTrivialEps      = 1e-3f; // relative |aS - aUV| skip default
        private const float kPerShellUnfoldEps       = 1e-12f; // unfold offset sumSq floor
        private const float kPerShellUvEps           = 1e-12f; // UV offset sumSq floor
        private const float kPerShellSanderCvSkip    = 0.5f;  // CV under which we trust the artist's UV0
        private const float kPerShellUnfoldCoverage  = 0.5f;  // min fraction of shell verts that must unfold

        /// <summary>
        /// Per-shell aspect normalization. For each shell, edge-unfolds the
        /// triangle strip into 2D to measure the **surface** aspect (rather
        /// than 3D-position PCA, which collapses on curved strips). Skips
        /// shells whose UV0 already preserves per-triangle area ratios
        /// (Sander L² CV &lt; 0.5 → trust the artist). For shells that need
        /// correction, aligns the UV principal axis to the unfold principal
        /// axis via a 2×2 SVD between the centered UV offsets and the
        /// unfold offsets, then applies an area-preserving non-uniform
        /// scale around the UV centroid so the UV bbox aspect in the aligned
        /// frame equals the surface aspect clamped to [1, maxAspect].
        /// Finally rotates back; the UV centroid is preserved.
        /// </summary>
        private static void ApplyPerShellAspect(
            float[] uvFlat,
            List<UvShell> shells,
            int[] tris,
            Vector3[] positions,
            float scaleMin,
            float scaleMax,
            float maxAspect,
            float relativeTrivialThreshold,
            int[] skipShellIndices,
            ref bool anyModified)
        {
            int n = shells.Count;
            int uvLen = uvFlat.Length;

            int modified = 0;
            int skipDegenerate = 0;
            int skipTrivial = 0;          // surface aspect ≈ UV aspect already
            int skipAlreadyUniform = 0;   // Sander CV < threshold → trust artist
            int skipUnfoldFail = 0;       // BFS unfold did not cover enough verts
            int skipNumeric = 0;
            int skipClamp = 0;
            int skipExternal = 0;

            float maxAspectClamp = maxAspect > 1f ? maxAspect : 1f;

            // Build a lookup set so the caller can route ribbon-classified
            // shells to ARAP without per-shell aspect also operating on them
            // (running both crushes ribbon UVs into degenerate slivers).
            HashSet<int> skipSet = null;
            if (skipShellIndices != null && skipShellIndices.Length > 0)
            {
                skipSet = new HashSet<int>();
                for (int i = 0; i < skipShellIndices.Length; i++)
                    skipSet.Add(skipShellIndices[i]);
            }

            // Per-vertex 2D unfold positions. Reused across shells to avoid
            // GC; we keep a HashSet of which vertices have a valid entry
            // for the current shell. (Dictionary clear is O(count) anyway.)
            var unfold2D = new Dictionary<int, Vector2>(256);

            for (int si = 0; si < n; si++)
            {
                if (skipSet != null && skipSet.Contains(si))
                {
                    skipExternal++;
                    continue;
                }
                var shell = shells[si];
                if (shell?.vertexIndices == null || shell.vertexIndices.Count < 3 ||
                    shell.faceIndices == null || shell.faceIndices.Count < kPerShellMinTriangles)
                {
                    skipDegenerate++;
                    continue;
                }

                // Surface aspect via edge-based BFS unfold (developable
                // approximation). Replaces 3D PCA which underestimates
                // aspect on curved strips (σ1≈σ2≈σ3 for a U-shaped beam).
                unfold2D.Clear();
                if (!ComputeSurfaceAspectByUnfold(shell, tris, positions, unfold2D,
                        out float surfaceAspect, out Vector2 unfoldMin, out Vector2 unfoldMax,
                        out int unfoldedVertCount))
                {
                    skipUnfoldFail++;
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"[Repack] PerShellAspect: shell {si} unfold incomplete → skipping");
                    continue;
                }

                // Minimum coverage — disconnected components or degenerate
                // adjacency may leave most verts un-placed.
                int totalVerts = shell.vertexIndices.Count;
                if (totalVerts <= 0 || (float)unfoldedVertCount / totalVerts < kPerShellUnfoldCoverage)
                {
                    skipUnfoldFail++;
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"[Repack] PerShellAspect: shell {si} unfold incomplete " +
                        $"({unfoldedVertCount}/{totalVerts} verts placed) → skipping");
                    continue;
                }

                float uw = unfoldMax.x - unfoldMin.x;
                float uh = unfoldMax.y - unfoldMin.y;
                if (uw < 1e-6f || uh < 1e-6f || surfaceAspect < kPerShellMinAspectRatio)
                {
                    skipDegenerate++;
                    continue;
                }

                float aspectSurface = Mathf.Clamp(surfaceAspect, 1f, maxAspectClamp);

                // Sander L² CV — if the artist's UV0 already preserves
                // per-triangle area ratios then triangles are well-shaped
                // and we should not disturb them. Threshold is empirical
                // (cv < 0.5 ≈ "uniform enough" given typical artist unwraps).
                float sanderCv = ComputeSanderCV(shell, tris, positions, uvFlat);
                if (sanderCv >= 0f && sanderCv < kPerShellSanderCvSkip)
                {
                    skipAlreadyUniform++;
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"[Repack] PerShellAspect: shell {si} surfaceAspect={aspectSurface:F2}:1 " +
                        $"cv={sanderCv:F2} (<{kPerShellSanderCvSkip:F2}) → already-uniform, skipping");
                    continue;
                }

                // UV centroid + paired unfold / UV offset accumulators for
                // the 2×2 cross-covariance.
                Vector2 cUv = Vector2.zero;
                int cn = 0;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    if (!unfold2D.ContainsKey(v)) continue;
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

                // Unfold centroid (over the placed verts only).
                Vector2 cFold = (unfoldMin + unfoldMax) * 0.5f;

                // M = Σ [uOff; vOff] · [fOff_x, fOff_y]
                double m00 = 0.0, m01 = 0.0, m10 = 0.0, m11 = 0.0;
                double fSumSq = 0.0, uSumSq = 0.0;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v * 2;
                    if ((uint)(idx + 1) >= (uint)uvLen) continue;
                    if (!unfold2D.TryGetValue(v, out Vector2 fp)) continue;

                    double f1 = fp.x - cFold.x;
                    double f2 = fp.y - cFold.y;

                    double uOff = uvFlat[idx]     - cUv.x;
                    double vOff = uvFlat[idx + 1] - cUv.y;

                    m00 += uOff * f1;
                    m01 += uOff * f2;
                    m10 += vOff * f1;
                    m11 += vOff * f2;

                    fSumSq += f1 * f1 + f2 * f2;
                    uSumSq += uOff * uOff + vOff * vOff;
                }

                if (fSumSq < kPerShellUnfoldEps || uSumSq < kPerShellUvEps)
                {
                    skipDegenerate++;
                    continue;
                }

                // 2×2 SVD → rotation angle θ that aligns UV principal axis
                // with the unfold's principal axis. R = U·V^T. We only need θ.
                if (!Compute2DRotationAngle(m00, m01, m10, m11, out double theta))
                {
                    skipNumeric++;
                    continue;
                }

                // Rotate UV offsets by −θ around cUv → axis-align with the
                // unfold's principal axis. Measure aligned bbox.
                float cosT = (float)Math.Cos(-theta);
                float sinT = (float)Math.Sin(-theta);

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

                // If the principal axis ended up shorter than the secondary,
                // the SVD picked the wrong column — rotate by +π/2 and
                // recompute. (Same swap protector as the previous version.)
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

                float denom = Mathf.Max(aspectSurface, aspectUV);
                float threshold = relativeTrivialThreshold > 0f
                    ? relativeTrivialThreshold
                    : kPerShellTrivialEps;
                if (denom > 0f && Mathf.Abs(aspectSurface - aspectUV) / denom < threshold)
                {
                    skipTrivial++;
                    continue;
                }

                // Area-preserving non-uniform scale: along A (principal) by
                // sx = √(aspectSurface / aspectUV), perpendicular by sy = 1/sx.
                float sx = Mathf.Sqrt(aspectSurface / aspectUV);
                float sy = aspectUV > 0f ? Mathf.Sqrt(aspectUV / aspectSurface) : 0f;
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
                // R(−θ) = [[cosT, -sinT],[sinT, cosT]]
                // diag·R(−θ) = [[sx*cosT, -sx*sinT],[sy*sinT, sy*cosT]]
                // R(+θ) * that:
                //   row0 = cosP*[sx*cosT,-sx*sinT] - sinP*[sy*sinT, sy*cosT]
                //   row1 = sinP*[sx*cosT,-sx*sinT] + cosP*[sy*sinT, sy*cosT]
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
                    $"[Repack] PerShellAspect: shell {si} surfaceAspect={aspectSurface:F2}:1 " +
                    $"UV={aspectUV:F2}:1 cv={sanderCv:F2} " +
                    $"θ={theta * (180.0 / Math.PI):F1}° scale=({sx:F3},{sy:F3})");
            }

            if (modified > 0) anyModified = true;

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Repack] PerShellAspect: normalized {modified}/{n} shells " +
                $"(skipped {skipDegenerate} degenerate, {skipTrivial} trivial, " +
                $"{skipAlreadyUniform} already-uniform [cv<{kPerShellSanderCvSkip:F2}], " +
                $"{skipUnfoldFail} unfold-incomplete, " +
                $"{skipNumeric} numeric, {skipClamp} out-of-scale-range, " +
                $"{skipExternal} caller-skipped)");
        }

        /// <summary>
        /// Edge-based BFS unfold of a shell's triangle strip into 2D. Hinges
        /// each newly visited triangle across its shared edge with an
        /// already-placed parent, choosing the half-plane opposite the
        /// parent's third vertex so the unfold never folds back on itself.
        /// For developable surfaces (cylinders unrolled to strips, cones,
        /// planes) the unfold is exact; for slightly curved surfaces it is
        /// a good approximation; for strongly curved surfaces (spheres) it
        /// overestimates aspect, which is still strictly better than 3D-PCA
        /// (which underestimates).
        ///
        /// Outputs `out2D` populated with global-vertex-index → 2D position
        /// for every vertex reachable via triangle adjacency from the seed
        /// triangle. Disconnected components beyond the seed component are
        /// not placed; the caller decides whether the coverage is enough.
        /// Returns false only when the seed triangle itself is degenerate.
        /// </summary>
        private static bool ComputeSurfaceAspectByUnfold(
            UvShell shell, int[] tris, Vector3[] positions,
            Dictionary<int, Vector2> out2D,
            out float surfaceAspect, out Vector2 bboxMin, out Vector2 bboxMax,
            out int unfoldedVertexCount)
        {
            surfaceAspect = 0f;
            bboxMin = new Vector2(float.MaxValue, float.MaxValue);
            bboxMax = new Vector2(float.MinValue, float.MinValue);
            unfoldedVertexCount = 0;

            int posLen = positions.Length;
            int trisLen = tris.Length;
            var faceIndices = shell.faceIndices;
            int faceCount = faceIndices.Count;

            // Gather valid triangle vertex triples (skipping out-of-range
            // and zero-area). localTriV[i*3+{0,1,2}] holds the three global
            // vertex indices of the i-th valid (local) triangle.
            var localTriV = new List<int>(faceCount * 3);

            for (int li = 0; li < faceCount; li++)
            {
                int f = faceIndices[li];
                int t = f * 3;
                if ((uint)(t + 2) >= (uint)trisLen) continue;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;

                Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                if (Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude < 1e-24f) continue;

                localTriV.Add(i0); localTriV.Add(i1); localTriV.Add(i2);
            }

            int triCount = localTriV.Count / 3;
            if (triCount < kPerShellMinTriangles) return false;

            // Edge → adjacent local triangle list. Edge key is (min,max) of
            // global vertex indices packed into a ulong.
            var edgeToTris = new Dictionary<ulong, List<int>>(triCount * 3);
            for (int li = 0; li < triCount; li++)
            {
                int a = localTriV[li * 3];
                int b = localTriV[li * 3 + 1];
                int c = localTriV[li * 3 + 2];
                AddEdge(edgeToTris, a, b, li);
                AddEdge(edgeToTris, b, c, li);
                AddEdge(edgeToTris, a, c, li);
            }

            // BFS from triangle 0 (first valid triangle of the shell).
            var visited = new bool[triCount];
            var queue   = new Queue<int>(triCount);

            // Lay out seed triangle in 2D.
            int s0 = localTriV[0], s1 = localTriV[1], s2 = localTriV[2];
            Vector3 sp0 = positions[s0], sp1 = positions[s1], sp2 = positions[s2];
            Vector3 e01 = sp1 - sp0;
            float l01 = e01.magnitude;
            if (l01 < 1e-12f) return false;

            // v0 = (0,0); v1 = (l01, 0); v2 placed in upper half-plane.
            Vector2 p2D_0 = Vector2.zero;
            Vector2 p2D_1 = new Vector2(l01, 0f);
            Vector3 e01n = e01 / l01;
            Vector3 e02 = sp2 - sp0;
            float dotProj = Vector3.Dot(e02, e01n);
            float perpLen2 = e02.sqrMagnitude - dotProj * dotProj;
            if (perpLen2 < 0f) perpLen2 = 0f;
            float perpLen = Mathf.Sqrt(perpLen2);
            if (perpLen < 1e-12f) return false; // already filtered, defensive
            Vector2 p2D_2 = new Vector2(dotProj, perpLen);

            out2D[s0] = p2D_0;
            out2D[s1] = p2D_1;
            out2D[s2] = p2D_2;
            UpdateBbox(ref bboxMin, ref bboxMax, p2D_0);
            UpdateBbox(ref bboxMin, ref bboxMax, p2D_1);
            UpdateBbox(ref bboxMin, ref bboxMax, p2D_2);

            visited[0] = true;
            queue.Enqueue(0);

            while (queue.Count > 0)
            {
                int li = queue.Dequeue();
                int va = localTriV[li * 3];
                int vb = localTriV[li * 3 + 1];
                int vc = localTriV[li * 3 + 2];

                TryHinge(li, va, vb, vc, edgeToTris, localTriV, positions, visited, queue, out2D, ref bboxMin, ref bboxMax);
                TryHinge(li, vb, vc, va, edgeToTris, localTriV, positions, visited, queue, out2D, ref bboxMin, ref bboxMax);
                TryHinge(li, va, vc, vb, edgeToTris, localTriV, positions, visited, queue, out2D, ref bboxMin, ref bboxMax);
            }

            unfoldedVertexCount = out2D.Count;

            float w = bboxMax.x - bboxMin.x;
            float h = bboxMax.y - bboxMin.y;
            if (w < 1e-12f || h < 1e-12f) return false;

            surfaceAspect = w >= h ? w / h : h / w;
            return true;
        }

        /// <summary>
        /// Hinge the unique unvisited neighbour of triangle T (current) across
        /// the shared edge (eA, eB), placing the neighbour's third vertex on
        /// the half-plane opposite T's third vertex (vOpp). No-op if no such
        /// neighbour, if the edge is degenerate in 2D, or if the third vertex
        /// is already placed inconsistently.
        /// </summary>
        private static void TryHinge(
            int currentLocal, int eA, int eB, int vOpp,
            Dictionary<ulong, List<int>> edgeToTris,
            List<int> localTriV,
            Vector3[] positions,
            bool[] visited,
            Queue<int> queue,
            Dictionary<int, Vector2> out2D,
            ref Vector2 bboxMin, ref Vector2 bboxMax)
        {
            ulong key = EdgeKey(eA, eB);
            if (!edgeToTris.TryGetValue(key, out var adj)) return;

            // Find the first unvisited neighbour through this edge.
            int nextLocal = -1;
            for (int k = 0; k < adj.Count; k++)
            {
                int cand = adj[k];
                if (cand == currentLocal) continue;
                if (visited[cand]) continue;
                nextLocal = cand;
                break;
            }
            if (nextLocal < 0) return;

            // The neighbour's third vertex (not eA, not eB).
            int n0 = localTriV[nextLocal * 3];
            int n1 = localTriV[nextLocal * 3 + 1];
            int n2 = localTriV[nextLocal * 3 + 2];
            int vC = (n0 != eA && n0 != eB) ? n0 :
                     (n1 != eA && n1 != eB) ? n1 :
                                              n2;

            // 2D positions of the shared edge endpoints. Must already exist
            // because the current triangle has been placed in full.
            if (!out2D.TryGetValue(eA, out Vector2 pA)) return;
            if (!out2D.TryGetValue(eB, out Vector2 pB)) return;

            Vector2 e = pB - pA;
            float eLen = e.magnitude;
            if (eLen < 1e-12f)
            {
                // Degenerate edge in 2D — still mark visited so we don't loop.
                visited[nextLocal] = true;
                queue.Enqueue(nextLocal);
                return;
            }
            Vector2 eN = e / eLen;
            Vector2 ePerp = new Vector2(-eN.y, eN.x);

            // 3D distances from vC to eA and eB.
            Vector3 pA3 = positions[eA];
            Vector3 pB3 = positions[eB];
            Vector3 pC3 = positions[vC];
            float dCA = (pC3 - pA3).magnitude;
            float dCB = (pC3 - pB3).magnitude;

            // Solve for vC in 2D: |vC - pA| = dCA, |vC - pB| = dCB.
            // Coordinate along edge: t = (dCA² - dCB² + eLen²) / (2·eLen).
            // Perpendicular distance: h² = dCA² - t².
            float t = (dCA * dCA - dCB * dCB + eLen * eLen) / (2f * eLen);
            float h2 = dCA * dCA - t * t;
            if (h2 < 0f) h2 = 0f; // clamp tiny numerical underflow
            float hOff = Mathf.Sqrt(h2);

            // Determine the half-plane of vOpp (current triangle's third
            // vertex) relative to the edge, then place vC on the OPPOSITE
            // side so the unfold never folds back.
            if (!out2D.TryGetValue(vOpp, out Vector2 pOpp))
            {
                // Defensive — shouldn't happen because the current triangle's
                // verts are all placed. Treat as "place on +perp side".
                pOpp = pA - ePerp;
            }
            Vector2 oppDelta = pOpp - pA;
            float oppPerp = Vector2.Dot(oppDelta, ePerp);
            float sideSign = oppPerp >= 0f ? -1f : +1f;

            Vector2 pC2D = pA + eN * t + ePerp * (sideSign * hOff);

            // If vC was already placed earlier in the BFS (cycle), keep the
            // earlier placement — it's consistent with that branch's history.
            if (!out2D.ContainsKey(vC))
            {
                out2D[vC] = pC2D;
                UpdateBbox(ref bboxMin, ref bboxMax, pC2D);
            }

            visited[nextLocal] = true;
            queue.Enqueue(nextLocal);
        }

        private static void AddEdge(Dictionary<ulong, List<int>> map, int a, int b, int triLocal)
        {
            ulong key = EdgeKey(a, b);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(triLocal);
        }

        private static ulong EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((ulong)(uint)lo << 32) | (uint)hi;
        }

        private static void UpdateBbox(ref Vector2 min, ref Vector2 max, Vector2 p)
        {
            if (p.x < min.x) min.x = p.x;
            if (p.y < min.y) min.y = p.y;
            if (p.x > max.x) max.x = p.x;
            if (p.y > max.y) max.y = p.y;
        }

        /// <summary>
        /// Sander L² coefficient of variation: per-triangle ratio
        /// (A_3D / A_UV), summarized as stddev / mean. Low CV (well below
        /// 1) means the artist's UV0 preserves area ratios across the
        /// shell — triangles are proportionally sized — and we should not
        /// disturb them. Returns -1 if too few triangles are usable for a
        /// stable estimate.
        /// </summary>
        private static float ComputeSanderCV(UvShell shell, int[] tris, Vector3[] positions, float[] uvFlat)
        {
            int posLen = positions.Length;
            int trisLen = tris.Length;
            int uvLen = uvFlat.Length;

            // Two-pass: collect ratios, then mean+variance. Use a List so we
            // can skip degenerate UV-area triangles cleanly.
            var ratios = new List<double>(shell.faceIndices.Count);
            for (int li = 0; li < shell.faceIndices.Count; li++)
            {
                int f = shell.faceIndices[li];
                int t = f * 3;
                if ((uint)(t + 2) >= (uint)trisLen) continue;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;

                Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                double a3D = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                if (a3D < 1e-18) continue;

                int u0 = i0 * 2, u1 = i1 * 2, u2 = i2 * 2;
                if ((uint)(u0 + 1) >= (uint)uvLen ||
                    (uint)(u1 + 1) >= (uint)uvLen ||
                    (uint)(u2 + 1) >= (uint)uvLen) continue;
                double ax = uvFlat[u0],     ay = uvFlat[u0 + 1];
                double bx = uvFlat[u1],     by = uvFlat[u1 + 1];
                double cx = uvFlat[u2],     cy = uvFlat[u2 + 1];
                double aUV = Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
                if (aUV < 1e-18) continue;

                ratios.Add(a3D / aUV);
            }

            if (ratios.Count < 3) return -1f;

            double mean = 0.0;
            for (int i = 0; i < ratios.Count; i++) mean += ratios[i];
            mean /= ratios.Count;
            if (mean <= 1e-18) return -1f;

            double variance = 0.0;
            for (int i = 0; i < ratios.Count; i++)
            {
                double d = ratios[i] - mean;
                variance += d * d;
            }
            variance /= ratios.Count;

            double stddev = Math.Sqrt(variance);
            return (float)(stddev / mean);
        }

        /// <summary>
        /// 2×2 SVD reduced to the rotation angle θ such that U·V^T = R(θ) =
        /// [[cosθ,-sinθ],[sinθ,cosθ]], where M = U·Σ·V^T. Derivation:
        ///   • S = M^T·M is 2×2 symmetric; its eigenvectors form V's columns.
        ///   • The principal eigenvector of S (largest eigenvalue) gives the
        ///     direction in the *input* space (= unfold-plane axes here)
        ///     that M maps to the largest principal direction in the output
        ///     space (= UV plane).
        ///   • U[:,0] = M·V[:,0] / s1 (largest singular value).
        ///   • R = U·V^T rotates V's first column onto U's first column —
        ///     i.e. rotates the unfold-frame's principal axis into the
        ///     UV-frame's principal axis. We want the *inverse*: rotate UV
        ///     to be aligned with the unfold's principal axis, so the
        ///     caller applies R(−θ).
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
