// XatlasRepack.cs — High-level xatlas repack with C#-side UV2 write-back
// Place in Assets/Editor/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public struct RepackOptions
    {
        public uint padding;        // inter-shell padding (pixels)
        public uint borderPadding;   // atlas edge padding (pixels), default 0
        public uint resolution;
        public float texelsPerUnit;
        /// <summary>Max chart dimension in pixels. 0 = no limit; xatlas may emit a
        /// chart larger than the atlas which then forces atlas growth. Set to a value
        /// ≤ resolution (e.g. resolution/2 or resolution) to keep individual charts
        /// inside the requested atlas size.</summary>
        public int maxChartSize;
        public bool bilinear;
        public bool blockAlign;
        /// <summary>
        /// Compression block size in texels for blockAlign. xatlas snaps charts
        /// to a hard-coded 4×4 grid (correct for BC1/BC3/BC5/BC7/ETC2/DXT*).
        /// For ASTC the block is variable (4, 5, 6, 8, 10, 12). The field is
        /// surfaced here and in the UI so projects can declare their target
        /// alignment; the actual post-pack snap to non-4 grids is not wired
        /// yet — at default 4 behaviour matches xatlas exactly, and for >4 a
        /// follow-up will add per-chart snapping after pack. Tracked as TODO.
        /// </summary>
        public int blockSize;
        public bool bruteForce;
        /// <summary>xatlas may rotate charts to fit better during packing (default true).</summary>
        public bool rotateCharts;
        /// <summary>Constrain chart rotation to axis-aligned 90° increments (default true).</summary>
        public bool rotateChartsToAxis;
        /// <summary>
        /// Per-group-member UV0 scale offset used to break xatlas's chart
        /// dedup on tile-instance shells (so each tile occupies a unique
        /// atlas slot — required for lightmap UV2 uniqueness).
        /// 0 = adaptive: derive from atlas resolution + padding.
        /// >0 = manual override, where N means each subsequent shell in an
        /// overlap-group is scaled around the rep's centroid by 1 + (i*N).
        /// Typical adaptive output: 0.01..0.03. Larger values handle more
        /// aggressive xatlas dedup at the cost of texel-density variance
        /// within the group.
        /// </summary>
        public float perturbStrength;

        /// <summary>
        /// Pre-pack pass that rescales each shell's UV0 so its UV-area is
        /// proportional to its 3D surface area (uniform texels-per-world-unit
        /// in the final lightmap). Without this, authored UV0 with mixed
        /// scales propagates straight to UV2 and the baked lightmap has
        /// detail varying by 10x+ across the model. Default ON for lightmap
        /// use. Disable only when preserving an existing baked-texture UV
        /// layout that already encodes desired non-uniform density.
        /// </summary>
        public bool normalizeTexelDensity;

        /// <summary>
        /// Auto-reparameterize shells whose UV0 stretch (Sander L² metric) exceeds
        /// <see cref="stretchThreshold"/>. Replaces the previous IsRibbon-based
        /// trigger — now driven by an actual UV quality metric. ARAP local-global
        /// solver redistributes vertices to minimize per-triangle isometric
        /// distortion.
        /// </summary>
        public bool reparameterizeStretchedShells;

        /// <summary>
        /// Sander L² stretch above which a shell triggers ARAP re-parameterization.
        /// 1.0 = isometric; 1.5 = mild; 2.0 = noticeably stretched; 3.0+ = severe.
        /// Default 1.5.
        /// </summary>
        public float stretchThreshold;

        /// <summary>
        /// ARAP local-global iteration count for stretched-shell reparameterization.
        /// 50 is the default; raise to 100-200 for highly curved/twisted strips.
        /// </summary>
        public int arapIterations;

        /// <summary>
        /// Clamp the source mesh's UV2 channel into [0,1] right after xatlas
        /// writes the atlas. Cheap safety net against verts pushed slightly
        /// outside the unit square by border padding or perturb fixups.
        /// Default true.
        /// </summary>
        public bool clampLightmapToUnit;

        /// <summary>
        /// Post-pack density correction (experimental). xatlas internally applies
        /// per-chart ceil(extents) stretch which breaks uniform density for thin
        /// shells. This pass measures per-shell au2/a3 after pack and SHRINKS
        /// over-dense shells around their UV2 centroid to match the median.
        /// Shrink-only (never expands) so neighbours can't collide. Default false.
        /// </summary>
        public bool postPackDensityCorrection;

        /// <summary>
        /// Internal xatlas atlas oversampling factor. xatlas applies an
        /// unconditional per-chart ceil(extents.x/y) stretch (xatlas.cpp:8345-
        /// 8362) that amplifies thin shells (extent &lt; 1 atlas-pixel) by up to
        /// 20×. Running the internal pack at a higher resolution makes every
        /// chart's pixel extents N× larger, so ceil() rounding becomes a
        /// fraction of a percent instead of multiples. Output UV2 is
        /// normalized to [0,1] regardless of internal atlas size so the user-
        /// facing resolution parameter still controls UV layout precision and
        /// the final lightmap baked by Unity is unaffected. Padding (both
        /// shell and border) is scaled by the same factor so the gap fraction
        /// in UV space stays constant. Default 8 — at 256×256 user-facing
        /// resolution the internal pack runs at 2048×2048 which brings
        /// per-shell density spread from ~14× down to ~1.1×.
        /// </summary>
        public int internalOversample;

        /// <summary>
        /// Snap each shell's pre-pack UV extents to integer atlas-pixel
        /// values via per-axis scale around the shell centroid. After this,
        /// xatlas's internal ceil(extents) rescale step (xatlas.cpp:8345-
        /// 8362, "Scale charts to use the entire texel area") becomes a
        /// no-op for every shell — uniform per-shell density survives the
        /// pack instead of being amplified by sub-pixel rounding.
        /// </summary>
        public bool snapShellsToIntegerPixels;

        /// <summary>
        /// Fraction of [0,1]² atlas that normalized UVs should sum to AFTER
        /// per-shell density equalisation. Bin-packing leaves slack, so
        /// total chart area below 1.0 keeps xatlas from overflowing the
        /// requested resolution and downscaling. Default 0.75 — 25% safety
        /// margin matching typical bruteForce packing efficiency on
        /// mixed-aspect chart sets. 0 disables the budget step (preserves
        /// original total UV area). Only used when normalizeTexelDensity
        /// is true.
        /// </summary>
        public float targetUvCoverage;

        public static RepackOptions Default => new RepackOptions
        {
            padding    = 4,
            borderPadding = 0,
            resolution = 0,
            texelsPerUnit = 0f,
            maxChartSize = 0,                 // 0 = unbounded
            bilinear   = true,
            blockAlign = false,
            blockSize  = 4,                   // BC/ETC/DXT default; ASTC: 4/5/6/8/10/12
            bruteForce = true,
            rotateCharts = true,
            rotateChartsToAxis = true,
            perturbStrength = 0f,             // adaptive
            normalizeTexelDensity = true,
            targetUvCoverage = 0.75f,
            reparameterizeStretchedShells = true,
            stretchThreshold = 1.5f,
            arapIterations = 50,
            clampLightmapToUnit = true,
            postPackDensityCorrection = false,
            internalOversample = 1,
            snapShellsToIntegerPixels = true,
        };
    }

    public struct RepackResult
    {
        public bool ok;
        public uint atlasWidth;
        public uint atlasHeight;
        public uint chartCount;
        public int  shellCount;
        public int  overlapGroupCount;
        public int  conflictVertices;
        public int  orphanVertices;
        public int  orphanTriangles;
        public int  snappedVertices;
        public int  flippedShells;
        public string error;
    }

    public static class XatlasRepack
    {
        const uint ORPHAN_CHART = uint.MaxValue;

        /// <summary>
        /// Flip UV0 shells with negative signed area (mirrored) so all charts
        /// have positive winding before xatlas packing.
        /// Modifies uv0 array in-place. Returns number of shells flipped.
        /// </summary>
        public static int NormalizeShellWinding(Vector2[] uv0, int[] tris, List<UvShell> shells)
        {
            int flipped = 0;
            foreach (var shell in shells)
            {
                // Compute signed area
                double area = 0;
                foreach (int f in shell.faceIndices)
                {
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    if (i0 >= uv0.Length || i1 >= uv0.Length || i2 >= uv0.Length) continue;
                    area += (uv0[i1].x - uv0[i0].x) * (uv0[i2].y - uv0[i0].y)
                          - (uv0[i2].x - uv0[i0].x) * (uv0[i1].y - uv0[i0].y);
                }
                if (area >= 0) continue; // positive winding — ok

                // Shell is mirrored → flip U around AABB center
                float centerU = (shell.boundsMin.x + shell.boundsMax.x) * 0.5f;
                float twoCenter = centerU * 2f;
                foreach (int vi in shell.vertexIndices)
                {
                    if (vi >= uv0.Length) continue;
                    uv0[vi] = new Vector2(twoCenter - uv0[vi].x, uv0[vi].y);
                }
                // Update shell bounds after flip
                float oldMinX = shell.boundsMin.x, oldMaxX = shell.boundsMax.x;
                shell.boundsMin = new Vector2(twoCenter - oldMaxX, shell.boundsMin.y);
                shell.boundsMax = new Vector2(twoCenter - oldMinX, shell.boundsMax.y);
                flipped++;
            }
            return flipped;
        }

        /// <summary>
        /// Pre-repack: apply tiny asymmetric UV0.x scale to overlap group members
        /// (except the first) so xatlas sees distinct chart shapes and avoids
        /// packing identical SymSplit halves at the same atlas position.
        /// Operates on the flat UV0 copy — does NOT modify the original mesh.
        /// </summary>
        /// <summary>
        /// Adaptive default for tile-instance UV0 perturbation strength.
        /// Goal: each subsequent shell in an overlap-group needs to differ
        /// from the previous one by at least a few atlas pixels after
        /// pixel-quantization, otherwise xatlas's bin-packer dedups them
        /// onto the same atlas slot (which collapses lightmap UV2). Smaller
        /// atlases quantize coarser → need bigger perturbation. Padding
        /// scales the floor because xatlas absorbs sub-padding differences.
        /// Output clamped to [0.01, 0.05] — 1% is enough at high resolution,
        /// 5% is the largest we accept before texel density variance becomes
        /// visible in baked lightmaps.
        /// </summary>
        internal static float ComputeAdaptivePerturbStrength(uint atlasResolution, uint padding)
        {
            const float MIN_STRENGTH = 0.01f;
            const float MAX_STRENGTH = 0.05f;
            if (atlasResolution == 0) return MIN_STRENGTH * 2f; // sensible fallback
            // 4 padding-pixels of UV-space separation per group step keeps
            // perturbed charts distinguishable through xatlas's quantization.
            float padPixels = Mathf.Max(1f, padding);
            float adaptive = 4f * padPixels / atlasResolution;
            return Mathf.Clamp(adaptive, MIN_STRENGTH, MAX_STRENGTH);
        }

        internal static void PerturbOverlapShellsUv0(
            float[] uvFlat, List<UvShell> shells, List<List<int>> overlapGroups,
            float strength)
        {
            if (overlapGroups == null || overlapGroups.Count == 0)
                return;
            if (strength <= 0f) return;

            // Geometry is unaffected — uvFlat is a local copy fed to xatlas
            // only; mesh.uv stays untouched.
            foreach (var group in overlapGroups)
            {
                if (group.Count < 2) continue;

                // Compute centroid (U,V) of first shell to use as scale pivot.
                // Must scale BOTH axes uniformly — scaling U only flattens
                // shells (fan-shaped discs become horizontal slivers when an
                // N-fold symmetry group accumulates scale on each copy).
                // Uniform scale only changes size, not shape; xatlas still
                // sees them as distinct charts (different bbox dimensions).
                var firstShell = shells[group[0]];
                float pivotU = (firstShell.boundsMin.x + firstShell.boundsMax.x) * 0.5f;
                float pivotV = (firstShell.boundsMin.y + firstShell.boundsMax.y) * 0.5f;

                for (int g = 1; g < group.Count; g++)
                {
                    float scale = 1f + g * strength;
                    var shell = shells[group[g]];
                    foreach (int vi in shell.vertexIndices)
                    {
                        int idx = vi * 2;
                        if ((uint)idx + 1 < (uint)uvFlat.Length)
                        {
                            float u = uvFlat[idx];
                            float v = uvFlat[idx + 1];
                            uvFlat[idx]     = pivotU + (u - pivotU) * scale;
                            uvFlat[idx + 1] = pivotV + (v - pivotV) * scale;
                        }
                    }
                }
            }
        }

        static float ComputeShellUvAreaAbs(Vector2[] uv, int[] tris, UvShell shell)
        {
            double area2 = 0.0;
            foreach (int f in shell.faceIndices)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if ((uint)i0 >= (uint)uv.Length || (uint)i1 >= (uint)uv.Length || (uint)i2 >= (uint)uv.Length)
                    continue;
                var a = uv[i0];
                var b = uv[i1];
                var c = uv[i2];
                area2 += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return Mathf.Abs((float)area2 * 0.5f);
        }

        /// <summary>
        /// Run HardEdgeShellAnalyzer and emit a one-line summary + up to 10
        /// per-shell rows for shells that would split into ≥2 sub-components
        /// with ≥2 faces each (user rule: don't cut if the outcome is just
        /// unstitching one face from the rest). Pure logging — the result is
        /// not yet applied to faceShellIds. Threshold 45° matches the
        /// classic "smoothing-group / hard-edge" cutoff used by DCC tools.
        /// </summary>
        static void LogHardEdgeAnalysis(List<UvShell> shells, int[] tris, Vector3[] positions, string meshLabel)
        {
            var r = HardEdgeShellAnalyzer.Analyze(shells, tris, positions, angleThresholdDeg: 45f, minSubshellFaces: 2);
            if (r.shellsSplittable == 0)
            {
                if (r.shellsWithHardEdges > 0)
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"[HardEdgeAnalysis] '{meshLabel}': {r.shellsWithHardEdges}/{r.totalShellsAnalyzed} shells contain hard edges but none would split into ≥2 chunks — nothing to cut.");
                return;
            }
            UvtLog.Info(UvtLog.Category.Repack,
                $"[HardEdgeAnalysis] '{meshLabel}': {r.shellsSplittable}/{r.totalShellsAnalyzed} shell(s) would split into ≥2 sub-components on hard edges (45°). " +
                $"({r.shellsWithHardEdges} shells contain at least one hard edge.)");
            int n = Mathf.Min(r.splittable.Count, 10);
            for (int i = 0; i < n; i++)
            {
                var info = r.splittable[i];
                UvtLog.Info(UvtLog.Category.Repack,
                    $"[HardEdgeAnalysis]   shell #{info.shellId}: {info.eligibleComponents} eligible sub-components (raw {info.totalComponents}, {info.hardEdgeCount} hard edge pairs)");
            }
            if (r.splittable.Count > n)
                UvtLog.Info(UvtLog.Category.Repack,
                    $"[HardEdgeAnalysis]   …and {r.splittable.Count - n} more.");
        }

        /// <summary>
        /// Sum of signed UV2 triangle areas across the mesh. uv2 is in [0,1]
        /// so the result equals the atlas utilization fraction (1.0 == 100%
        /// of the atlas covered by charts; bin-packing typically lands at
        /// 0.55–0.85 depending on chart shape mix and packer mode).
        /// </summary>
        internal static double ComputeUv2CoverageFraction(Vector2[] uv2, int[] tris)
        {
            if (uv2 == null || tris == null) return 0.0;
            double sum = 0.0;
            int triCount = tris.Length / 3;
            int uvLen = uv2.Length;
            for (int f = 0; f < triCount; f++)
            {
                int t = f * 3;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)uvLen || (uint)i1 >= (uint)uvLen || (uint)i2 >= (uint)uvLen) continue;
                Vector2 a = uv2[i0], b = uv2[i1], c = uv2[i2];
                sum += Math.Abs((double)(b.x - a.x) * (c.y - a.y) - (double)(c.x - a.x) * (b.y - a.y)) * 0.5;
            }
            return sum;
        }

        /// <summary>
        /// Per-shell post-xatlas UV2 density diagnostic. Groups output UV2 by
        /// original UV0 shell (via shells[].faceIndices) and logs au2/a3
        /// distribution. If TexelDensityNormalizer made input shell densities
        /// uniform but post-pack densities still vary, xatlas internally
        /// rescaled charts — which means the upstream density correction was
        /// thrown away by the pack step.
        /// </summary>
        static void LogPostPackDensity(
            Vector2[] uv2, int[] tris, Vector3[] positions,
            List<UvShell> shells, string meshLabel)
        {
            if (uv2 == null || tris == null || positions == null || shells == null) return;
            int uvLen = uv2.Length;
            int posLen = positions.Length;
            int n = shells.Count;
            if (n == 0) return;

            double dMin = double.MaxValue, dMax = 0.0, dSum = 0.0;
            int dCount = 0;
            double minRatio = double.MaxValue, maxRatio = 0.0;
            int shellIdMin = -1, shellIdMax = -1;
            for (int si = 0; si < n; si++)
            {
                var shell = shells[si];
                if (shell?.faceIndices == null) continue;
                double a3 = 0.0, au = 0.0;
                foreach (int f in shell.faceIndices)
                {
                    int t = f * 3;
                    if ((uint)(t + 2) >= (uint)tris.Length) continue;
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                    if ((uint)i0 >= (uint)uvLen || (uint)i1 >= (uint)uvLen || (uint)i2 >= (uint)uvLen) continue;
                    Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                    a3 += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                    Vector2 a = uv2[i0], b = uv2[i1], c = uv2[i2];
                    au += Math.Abs((double)(b.x - a.x) * (c.y - a.y) - (double)(c.x - a.x) * (b.y - a.y)) * 0.5;
                }
                if (a3 < 1e-12 || au < 1e-12) continue;
                double d = au / a3;
                if (d < dMin) { dMin = d; shellIdMin = si; }
                if (d > dMax) { dMax = d; shellIdMax = si; }
                dSum += d;
                dCount++;
            }

            if (dCount == 0) return;
            double dMean = dSum / dCount;
            double ratio = (dMin > 1e-30) ? dMax / dMin : 0.0;

            // Spread density across shells around the mean — a few outliers
            // can swamp maxRatio, so also report how the bulk behaves.
            int withinTenPct = 0;
            for (int si = 0; si < n; si++)
            {
                var shell = shells[si];
                if (shell?.faceIndices == null) continue;
                double a3 = 0.0, au = 0.0;
                foreach (int f in shell.faceIndices)
                {
                    int t = f * 3;
                    if ((uint)(t + 2) >= (uint)tris.Length) continue;
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                    if ((uint)i0 >= (uint)uvLen || (uint)i1 >= (uint)uvLen || (uint)i2 >= (uint)uvLen) continue;
                    Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                    a3 += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                    Vector2 a = uv2[i0], b = uv2[i1], c = uv2[i2];
                    au += Math.Abs((double)(b.x - a.x) * (c.y - a.y) - (double)(c.x - a.x) * (b.y - a.y)) * 0.5;
                }
                if (a3 < 1e-12 || au < 1e-12) continue;
                double d = au / a3;
                if (Math.Abs(d - dMean) <= 0.10 * dMean) withinTenPct++;
            }

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Density:postUV2] '{meshLabel}' shells={dCount} | au2/a3: min={dMin:G3}(shell#{shellIdMin}) max={dMax:G3}(shell#{shellIdMax}) mean={dMean:G3} maxRatio={ratio:F2}x | within±10%: {withinTenPct}/{dCount}");
        }

        /// <summary>
        /// Shrink-only post-pack density correction. After xatlas packs charts
        /// into the atlas its internal ceil(extents) stretch (xatlas.cpp:8345-
        /// 8362) amplifies thin/anisotropic shells more than fat ones, breaking
        /// the uniform density we set up in TexelDensityNormalizer. This pass
        /// measures per-shell au2/a3 and shrinks shells whose density is above
        /// the median toward it, keeping each shell anchored on its UV2
        /// centroid. Shrink only — never expand — so the layout stays valid
        /// (shells can't collide into neighbours). The atlas ends up with some
        /// gaps where shrunk shells used to be; this trades coverage for density
        /// uniformity, which is the correct trade for lightmap bake quality.
        /// </summary>
        static int ApplyPostPackDensityCorrection(
            Vector2[] uv2, int[] tris, Vector3[] positions,
            List<UvShell> shells, string meshLabel)
        {
            if (uv2 == null || tris == null || positions == null || shells == null) return 0;
            int uvLen = uv2.Length;
            int posLen = positions.Length;
            int n = shells.Count;
            if (n == 0) return 0;

            var a3Arr = new double[n];
            var au2Arr = new double[n];
            var densArr = new double[n];
            var validShells = new List<int>(n);
            for (int si = 0; si < n; si++)
            {
                var shell = shells[si];
                if (shell?.faceIndices == null) continue;
                double a3 = 0.0, au = 0.0;
                foreach (int f in shell.faceIndices)
                {
                    int t = f * 3;
                    if ((uint)(t + 2) >= (uint)tris.Length) continue;
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                    if ((uint)i0 >= (uint)uvLen || (uint)i1 >= (uint)uvLen || (uint)i2 >= (uint)uvLen) continue;
                    Vector3 p0 = positions[i0], p1 = positions[i1], p2 = positions[i2];
                    a3 += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5;
                    Vector2 a = uv2[i0], b = uv2[i1], c = uv2[i2];
                    au += Math.Abs((double)(b.x - a.x) * (c.y - a.y) - (double)(c.x - a.x) * (b.y - a.y)) * 0.5;
                }
                a3Arr[si] = a3;
                au2Arr[si] = au;
                if (a3 < 1e-12 || au < 1e-12) continue;
                densArr[si] = au / a3;
                validShells.Add(si);
            }

            if (validShells.Count == 0) return 0;

            // Median density as the target — robust to a handful of extreme outliers
            // (degenerate L²=1000 shells) that would skew the mean.
            var sortedDens = new List<double>(validShells.Count);
            foreach (int si in validShells) sortedDens.Add(densArr[si]);
            sortedDens.Sort();
            double targetDensity = sortedDens[sortedDens.Count / 2];
            if (targetDensity < 1e-12) return 0;

            int modified = 0;
            double appliedScaleMin = 1.0, appliedScaleMax = 1.0;
            foreach (int si in validShells)
            {
                double density = densArr[si];
                if (density <= targetDensity * 1.05) continue; // within 5% of target — leave alone

                // Bring density down to target. au2 scales with scale²;
                // density_new = (au2 * scale²) / a3 = density * scale² = target
                // → scale = sqrt(target / density). Shrink only.
                double scaleD = Math.Sqrt(targetDensity / density);
                if (!IsFiniteD(scaleD) || scaleD <= 0.0) continue;
                if (scaleD >= 0.999) continue; // basically no-op
                float scale = (float)scaleD;
                if (scale < appliedScaleMin) appliedScaleMin = scale;
                if (scale > appliedScaleMax) appliedScaleMax = scale;

                var shell = shells[si];
                if (shell.vertexIndices == null || shell.vertexIndices.Count == 0) continue;

                // UV2 centroid (uniform shrink leaves the centroid fixed → the
                // shell stays where xatlas put it; only the bbox contracts
                // inward, so neighbours stay outside the shrunken bbox).
                Vector2 c = Vector2.zero;
                int cn = 0;
                foreach (int v in shell.vertexIndices)
                {
                    int idx = v;
                    if ((uint)idx >= (uint)uvLen) continue;
                    c.x += uv2[idx].x;
                    c.y += uv2[idx].y;
                    cn++;
                }
                if (cn == 0) continue;
                c.x /= cn;
                c.y /= cn;

                foreach (int v in shell.vertexIndices)
                {
                    int idx = v;
                    if ((uint)idx >= (uint)uvLen) continue;
                    Vector2 uv = uv2[idx];
                    uv2[idx] = new Vector2(
                        c.x + (uv.x - c.x) * scale,
                        c.y + (uv.y - c.y) * scale);
                }
                modified++;
            }

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Density:correction] '{meshLabel}' shrunk {modified}/{validShells.Count} over-dense shells toward median={targetDensity:G3} | applied scale: min={appliedScaleMin:F3} max={appliedScaleMax:F3}");
            return modified;
        }

        static bool IsFiniteD(double x) => !(double.IsNaN(x) || double.IsInfinity(x));

        /// <summary>
        /// Pre-snap each shell so its UV bbox extent is an integer number of
        /// atlas texels at the supplied <paramref name="tpu"/> (texels-per-unit)
        /// scale. xatlas internally rescales every chart to ceil(extents *
        /// tpu) (xatlas.cpp:8345-8362, upstream Issue #18 wontfix); by making
        /// the extents already integer-pixel-aligned at the *same* tpu that
        /// is then passed to xatlasPackCharts, that ceil() becomes a no-op
        /// for every chart and the uniform density set up by
        /// TexelDensityNormalizer survives the pack.
        ///
        /// Per-axis scale around the shell centroid: scaleX = ceil(w_px)/w_px,
        /// scaleY = ceil(h_px)/h_px. Both factors are ≥1, typically ≤ 1+1/w_px
        /// (≪3% for normal shells, larger only for sub-pixel ribbons — which
        /// already had broken density anyway). Centroid-anchored scale keeps
        /// neighbouring shells from colliding in the pre-pack layout.
        ///
        /// Operates on the same flat UV buffer that's about to be handed to
        /// xatlas. Returns the number of shells whose extents needed adjustment.
        /// </summary>
        static int SnapShellsToIntegerPixels(
            float[] uvFlat, List<UvShell> shells, float tpu, string meshLabel)
        {
            if (uvFlat == null || shells == null || !(tpu > 0f)) return 0;
            int vertCount = uvFlat.Length / 2;
            int snapped = 0;
            double maxScale = 1.0;
            foreach (var shell in shells)
            {
                if (shell?.vertexIndices == null || shell.vertexIndices.Count == 0) continue;

                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                foreach (int vi in shell.vertexIndices)
                {
                    if ((uint)vi >= (uint)vertCount) continue;
                    float u = uvFlat[vi * 2];
                    float v = uvFlat[vi * 2 + 1];
                    if (u < minX) minX = u;
                    if (u > maxX) maxX = u;
                    if (v < minY) minY = v;
                    if (v > maxY) maxY = v;
                }
                if (!(maxX > minX) || !(maxY > minY)) continue;

                float wPx = (maxX - minX) * tpu;
                float hPx = (maxY - minY) * tpu;
                if (wPx <= 0f || hPx <= 0f) continue;

                float targetW = Mathf.Ceil(wPx);
                float targetH = Mathf.Ceil(hPx);
                float scaleX = targetW / wPx;
                float scaleY = targetH / hPx;

                // Skip when both axes are already pixel-aligned within FP noise.
                if (Mathf.Abs(scaleX - 1f) < 1e-6f && Mathf.Abs(scaleY - 1f) < 1e-6f)
                    continue;

                float cx = (minX + maxX) * 0.5f;
                float cy = (minY + maxY) * 0.5f;
                foreach (int vi in shell.vertexIndices)
                {
                    if ((uint)vi >= (uint)vertCount) continue;
                    int idx = vi * 2;
                    uvFlat[idx]     = cx + (uvFlat[idx]     - cx) * scaleX;
                    uvFlat[idx + 1] = cy + (uvFlat[idx + 1] - cy) * scaleY;
                }

                double sMax = Math.Max(scaleX, scaleY);
                if (sMax > maxScale) maxScale = sMax;
                snapped++;
            }
            if (snapped > 0)
                UvtLog.Info(UvtLog.Category.Repack,
                    $"[Density:snap] '{meshLabel}' snapped {snapped}/{shells.Count} shells to integer atlas-pixel extents (tpu={tpu:F2}, maxScale={maxScale:F4})");
            return snapped;
        }

        /// <summary>
        /// Compute the effective texels-per-unit value that will be passed to
        /// xatlasPackCharts when <see cref="RepackOptions.snapShellsToIntegerPixels"/>
        /// is on. Must match the grid the snap step aligns to — otherwise
        /// xatlas's internal ceil(extents * tpu) rescale (Stage B) will fire
        /// again and undo the snap.
        ///
        /// After <see cref="TexelDensityNormalizer"/> the total UV chart area
        /// equals <c>targetUvCoverage</c> (e.g. 0.75). We want the same total
        /// area in texel space to occupy <c>internalRes² × targetCoverage</c>,
        /// which gives <c>tpu = internalRes × sqrt(targetCoverage)</c>. With
        /// this tpu xatlas does not need to expand the atlas to fit, so the
        /// integer-pixel grid the snap aligned to is the grid xatlas packs at.
        /// </summary>
        static float ComputeEffectiveTpu(RepackOptions opts, uint internalRes)
        {
            if (opts.texelsPerUnit > 0f) return opts.texelsPerUnit;
            if (internalRes == 0) return 0f;
            float cov = opts.targetUvCoverage > 0f ? opts.targetUvCoverage : 0.75f;
            return internalRes * Mathf.Sqrt(cov);
        }

        /// <summary>
        /// Run xatlas ComputeCharts + PackCharts on a background thread while
        /// the main thread polls a cancellable progress bar. xatlas itself
        /// can't be interrupted mid-pack (no native cancel API), so a "cancel"
        /// click here means: stop showing progress, wait for the in-flight
        /// pack to finish (since xatlas state is a process-wide singleton —
        /// can't safely abandon it), then return cancelled. The user gets
        /// reactive UI feedback even when the pack is slow.
        ///
        /// Returns true if pack completed normally, false if user cancelled
        /// (caller should clean up via xatlasDestroy and skip output read).
        /// </summary>
        static bool RunPackCancelable(
            string label, int shellCount, uint internalRes,
            int maxChartSize, uint padding, float texelsPerUnit, uint resolution,
            int bilinear, int blockAlign, int bruteForce,
            int rotateCharts, int rotateChartsToAxis)
        {
            // Cost preflight — refuse impossibly large packs up front instead
            // of hanging for hours. Brute force is O(shells × W × H); the
            // random heuristic is much cheaper but still scales with area.
            long packCost = (long)shellCount * (long)internalRes * (long)internalRes;
            const long kBruteCostBudget = 500_000_000L;     // ~5-10s wall
            const long kHeuristicCostBudget = 20_000_000_000L; // ~30-60s wall

            if (bruteForce != 0 && packCost > kBruteCostBudget)
            {
                UvtLog.Info(UvtLog.Category.Repack,
                    $"[xatlas] Brute force pack disabled — cost {packCost / 1_000_000L}M ops × {shellCount} shells × {internalRes}² atlas would exceed {kBruteCostBudget / 1_000_000L}M budget");
                bruteForce = 0;
            }
            if (packCost > kHeuristicCostBudget)
            {
                UvtLog.Warn(UvtLog.Category.Repack,
                    $"[xatlas] Pack cost {packCost / 1_000_000_000L}B ops is past the {kHeuristicCostBudget / 1_000_000_000L}B safety budget — refusing to start pack. Lower internal oversample or atlas resolution.");
                return false;
            }

            var packTask = Task.Run(() =>
            {
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(
                    maxChartSize, padding, texelsPerUnit, resolution,
                    bilinear, blockAlign, bruteForce,
                    rotateCharts, rotateChartsToAxis);
            });

            double startTime = EditorApplication.timeSinceStartup;
            bool cancelled = false;
            try
            {
                while (!packTask.IsCompleted)
                {
                    double elapsed = EditorApplication.timeSinceStartup - startTime;
                    string msg = $"{label} — atlas {internalRes}×{internalRes}, {shellCount} shells, {elapsed:F0}s elapsed";
                    if (EditorUtility.DisplayCancelableProgressBar("xatlas pack", msg, -1f))
                    {
                        cancelled = true;
                        break;
                    }
                    System.Threading.Thread.Sleep(50);
                }

                // xatlas has no native cancel — wait for the task to actually
                // finish before returning, otherwise the singleton state is
                // mid-mutation and the next xatlasCreate would race against it.
                packTask.Wait();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (cancelled)
                UvtLog.Warn(UvtLog.Category.Repack, "[xatlas] Pack cancelled by user (xatlas finished its in-flight operation; result discarded)");

            return !cancelled;
        }

        /// <summary>
        /// Raw xatlas output density diagnostic. Operates on the buffers returned
        /// by xatlasGetOutputVertexData/Indices (atlas-pixel space, grouped by
        /// xatlas chart ID, not by our original shell IDs).
        ///
        /// xatlas applies a single global texelsPerUnit scale to every chart
        /// (sqrt(surfaceArea/parametricArea) == 1 for UvMesh input), so per-chart
        /// UV-area ratios should match the *input* UV-area ratios exactly. If
        /// they don't, something inside xatlas's pack stage (maxChartSize clamp,
        /// per-chart rotation+fit, sub-atlas split) altered them.
        ///
        /// Note: this measures area per xatlas chart, NOT per original shell.
        /// A shell that xatlas split into N charts will appear as N rows here.
        /// </summary>
        static void LogRawXatlasDensity(
            float[] outUV, uint[] outChart, uint[] outIdx,
            int outVertCount, int outIndexCount,
            string meshLabel)
        {
            if (outUV == null || outChart == null || outIdx == null) return;
            int triCount = outIndexCount / 3;
            if (triCount == 0) return;

            // Build chart → list of triangle areas.
            var chartArea = new Dictionary<uint, double>();
            for (int t = 0; t < triCount; t++)
            {
                int ti = t * 3;
                uint i0 = outIdx[ti], i1 = outIdx[ti + 1], i2 = outIdx[ti + 2];
                if (i0 >= (uint)outVertCount || i1 >= (uint)outVertCount || i2 >= (uint)outVertCount) continue;
                // Chart ID — pick from one vertex; all three should match for an unsplit tri.
                uint cId = outChart[i0];
                int u0 = (int)i0 * 2, u1 = (int)i1 * 2, u2 = (int)i2 * 2;
                double ax = outUV[u0],     ay = outUV[u0 + 1];
                double bx = outUV[u1],     by = outUV[u1 + 1];
                double cx = outUV[u2],     cy = outUV[u2 + 1];
                double a = Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) * 0.5;
                if (chartArea.TryGetValue(cId, out double cur))
                    chartArea[cId] = cur + a;
                else
                    chartArea[cId] = a;
            }

            if (chartArea.Count == 0) return;
            double cMin = double.MaxValue, cMax = 0.0, cSum = 0.0;
            foreach (var kv in chartArea)
            {
                if (kv.Value <= 0.0) continue;
                if (kv.Value < cMin) cMin = kv.Value;
                if (kv.Value > cMax) cMax = kv.Value;
                cSum += kv.Value;
            }
            double cMean = cSum / chartArea.Count;
            double cRatio = (cMin > 1e-30) ? cMax / cMin : 0.0;

            UvtLog.Info(UvtLog.Category.Repack,
                $"[Density:xatlasRaw] '{meshLabel}' charts={chartArea.Count} | chart UV-area (atlas-px²): min={cMin:G3} max={cMax:G3} mean={cMean:G3} maxRatio={cRatio:F2}x");
        }


        /// <summary>
        /// Convenience wrapper: repack UV0 shells into UV2, return packed UV2 array.
        /// Does NOT modify the original mesh.
        /// </summary>
        public static Vector2[] RepackUv(Mesh mesh, Vector2[] uv0, uint[] faceShellIds,
            int resolution, int padding, bool rotate)
        {
            var opts = RepackOptions.Default;
            opts.resolution = (uint)resolution;
            opts.padding = (uint)padding;
            // Work on a temporary copy so original mesh is untouched
            var tmp = UnityEngine.Object.Instantiate(mesh);
            tmp.name = mesh.name + "_repack_tmp";
            var result = RepackSingle(tmp, opts);
            if (!result.ok)
            {
                UnityEngine.Object.DestroyImmediate(tmp);
                return null;
            }
            var uvOut = new List<Vector2>();
            tmp.GetUVs(1, uvOut);
            UnityEngine.Object.DestroyImmediate(tmp);
            return uvOut.ToArray();
        }

        public static RepackResult RepackSingle(Mesh mesh, RepackOptions opts)
        {
            var result = new RepackResult();

            // ── Read mesh data ──
            Vector2[] uv0 = mesh.uv;
            if (uv0 == null || uv0.Length == 0)
            {
                result.error = "Mesh has no UV0";
                return result;
            }

            int[] tris = mesh.triangles;
            int vertCount = mesh.vertexCount;
            int faceCount = tris.Length / 3;

            // 3D vertex positions — needed by tile-merge guard to compare
            // shell 3D AABB size (rejects same-UV0-region shells whose 3D
            // shapes differ, e.g. wood-plank vs box-lid sharing the same
            // wood-texture UV0 region).
            Vector3[] positions = mesh.vertices;

            // ── Extract shells + build per-face shell IDs ──
            List<UvShell> shells;
            List<List<int>> overlapGroups;
            uint[] faceShellIds = UvShellExtractor.BuildPerFaceShellIds(
                uv0, tris, out shells, out overlapGroups);

            result.shellCount = shells.Count;
            result.overlapGroupCount = overlapGroups.Count;
            int overlapPairCount = UvShellExtractor.CountAabbOverlaps(shells);
            UvtLog.Verbose($"[xatlas] Pre-repack: {shells.Count} shells, " +
                $"{overlapGroups.Count} overlap groups, {overlapPairCount} overlapping pairs");

            // Hard-edge shell-split analysis. Pure diagnostic for now — the
            // result is logged but the perFaceComponent map is not applied to
            // faceShellIds. A future opt-in will materialise the split.
            LogHardEdgeAnalysis(shells, tris, mesh.vertices, meshLabel: mesh.name);

            // UV0 winding normalized by ExecWeldUv0.
            result.flippedShells = 0;

            // ── Flatten UV0 ──
            float[] uvFlat = new float[vertCount * 2];
            for (int i = 0; i < vertCount; i++)
            {
                uvFlat[i * 2]     = uv0[i].x;
                uvFlat[i * 2 + 1] = uv0[i].y;
            }

            // ── Pre-pack pipeline ──
            // Two stages:
            //   1. ARAP re-parameterization of stretched shells (Sander L²
            //      gate) — fixes per-shell distortion at the vertex level.
            //   2. Texel density correction (per-shell uniform scale) —
            //      equalises post-ARAP shell areas. Uniform scale doesn't
            //      distort the just-relaxed shapes.
            //
            // The earlier global-aspect bbox-to-1:1 pre-pass was removed:
            // xatlas does not require a 1:1 input UV0, and anisotropic global
            // scale only fought ARAP's per-shell output. Operates on the
            // local uvFlat copy; mesh.uv is untouched.

            if (opts.reparameterizeStretchedShells)
            {
                int stretchedFound = 0, converged = 0, skipped = 0;
                for (int si = 0; si < shells.Count; si++)
                {
                    var shell = shells[si];
                    if (shell?.vertexIndices == null || shell.vertexIndices.Count < 3) continue;
                    float l2 = ShellQuality.ComputeL2Stretch(shell, tris, positions, uvFlat);
                    if (float.IsNaN(l2) || l2 < opts.stretchThreshold) continue;
                    stretchedFound++;
                    var shellTriIndices = shell.faceIndices?.ToArray() ?? new int[0];
                    if (shellTriIndices.Length == 0) { skipped++; continue; }
                    if (ArapParameterization.Reparameterize(
                            positions, tris, shellTriIndices, shell.vertexIndices,
                            uvFlat, opts.arapIterations, out int _initFlipped))
                    {
                        converged++;
                        UvtLog.Verbose(UvtLog.Category.Repack,
                            $"[Repack] ARAP: shell {si} L²={l2:F2} (>{opts.stretchThreshold:F2}) → reparameterized");
                    }
                    else
                        skipped++;
                }
                if (stretchedFound > 0)
                    UvtLog.Info(UvtLog.Category.Repack,
                        $"[Repack] ARAP: reparameterized {converged}/{stretchedFound} stretched shells (L²>{opts.stretchThreshold:F2}, skipped {skipped})");
            }

            if (opts.normalizeTexelDensity)
            {
                // Normalize logs its own [Density] summary at Info level
                // (pre/post au/a3 distribution + scale spread).
                TexelDensityNormalizer.Normalize(
                    uvFlat, shells, tris, positions,
                    targetCoverage: opts.targetUvCoverage);
            }

            // ── Compute internal pack dims + effective tpu ──
            // Hoisted before snap so the snap step aligns to the *same*
            // texels-per-unit grid that xatlasPackCharts will use. xatlas's
            // per-chart ceil(extents * tpu) Stage B rescale only becomes a
            // no-op when the two grids agree; if xatlas computes its own
            // tpu (texelsPerUnit=0) and grows the atlas to fit, the snap
            // grid is wrong and density gets amplified anyway.
            int oversample = opts.internalOversample > 0 ? opts.internalOversample : 1;
            uint internalRes = opts.resolution * (uint)oversample;
            uint internalPad = opts.padding    * (uint)oversample;
            float effectiveTpu = ComputeEffectiveTpu(opts, internalRes);

            if (opts.snapShellsToIntegerPixels && effectiveTpu > 0f)
                SnapShellsToIntegerPixels(uvFlat, shells, effectiveTpu, mesh.name);

            // ── Perturb UV0 of overlap-grouped shells ──
            // xatlas dedups charts whose input UVs look identical and lays
            // them at the same atlas slot — catastrophic for lightmap UV2,
            // which must be unique per shell. A per-group-member pre-scale
            // around the rep's centroid breaks the tie so xatlas gives
            // every tile-instance its own atlas region. Strength is either
            // user-supplied via opts.perturbStrength or computed adaptively
            // from atlas resolution + padding.
            float perturbStrength = opts.perturbStrength > 0f
                ? opts.perturbStrength
                : ComputeAdaptivePerturbStrength(opts.resolution, opts.padding);
            PerturbOverlapShellsUv0(uvFlat, shells, overlapGroups, perturbStrength);

            // ── Group-aware merge of overlapping shells ──
            // Tiled-UV0 models (Wooden_Box_Long etc.) carry N>>K shells where
            // K-many representative patches are duplicated N times by tile
            // instancing — all overlapping in UV0. xatlas would pack the N
            // independent charts and leave most of the atlas empty. Instead,
            // we pick one representative per overlap-group, feed xatlas ONLY
            // Flatten triangle indices into the uint32 buffer xatlas wants.
            // Every shell is fed to xatlas as its own chart — UV2 must be
            // unique per shell (lightmap channel); the previous "merge
            // overlapping tiles" mode that collapsed tile-instances into one
            // shared chart was removed because it produced incorrect baked
            // lighting for instanced parts.
            uint[] indices = new uint[tris.Length];
            for (int i = 0; i < tris.Length; i++)
                indices[i] = (uint)tris[i];
            uint[] xatlasFaceShellIds = faceShellIds;
            uint   xatlasFaceCount    = (uint)faceCount;

            // ── xatlas pipeline ──
            XatlasNative.xatlasCreate();

            try
            {
                int addErr = XatlasNative.xatlasAddUvMesh(
                    uvFlat, (uint)vertCount,
                    indices, (uint)indices.Length,
                    xatlasFaceShellIds, xatlasFaceCount);

                if (addErr != 0)
                {
                    result.error = $"xatlasAddUvMesh error {addErr}";
                    return result;
                }

                // internalRes / internalPad / effectiveTpu were computed
                // above the snap step so the snap grid matches the pack
                // grid. Passing effectiveTpu (not opts.texelsPerUnit, which
                // is 0 by default) keeps xatlas from auto-computing its own
                // tpu and growing the atlas — that would invalidate the
                // integer-pixel grid the snap aligned to.
                bool packed = RunPackCancelable(
                    mesh.name, shells.Count, internalRes,
                    opts.maxChartSize, internalPad, effectiveTpu, internalRes,
                    opts.bilinear  ? 1 : 0,
                    opts.blockAlign ? 1 : 0,
                    opts.bruteForce ? 1 : 0,
                    opts.rotateCharts ? 1 : 0,
                    opts.rotateChartsToAxis ? 1 : 0);
                if (!packed)
                {
                    result.error = "cancelled";
                    return result;
                }

                if (XatlasNative.xatlasGetMeshCount() == 0)
                {
                    result.error = "xatlas returned 0 meshes";
                    return result;
                }

                result.atlasWidth  = XatlasNative.xatlasGetAtlasWidth();
                result.atlasHeight = XatlasNative.xatlasGetAtlasHeight();
                result.chartCount  = XatlasNative.xatlasGetChartCount();

                UvtLog.Info(UvtLog.Category.Repack,
                    $"xatlas pack '{mesh.name}': req={opts.resolution}, actual={result.atlasWidth}x{result.atlasHeight}, charts={result.chartCount}");

                // ── Get raw output data ──
                int outVertCount  = XatlasNative.xatlasGetOutputVertexCount(0);
                int outIndexCount = XatlasNative.xatlasGetOutputIndexCount(0);

                if (outVertCount == 0 || outIndexCount == 0)
                {
                    result.error = $"xatlas output empty: verts={outVertCount}, idx={outIndexCount}";
                    return result;
                }

                uint[]  outXref  = new uint[outVertCount];
                float[] outUV    = new float[outVertCount * 2];
                uint[]  outChart = new uint[outVertCount];
                uint[]  outIdx   = new uint[outIndexCount];

                XatlasNative.xatlasGetOutputVertexData(0, outXref, outUV, outChart, outVertCount);
                XatlasNative.xatlasGetOutputIndices(0, outIdx, outIndexCount);

                LogRawXatlasDensity(outUV, outChart, outIdx, outVertCount, outIndexCount, mesh.name);

                // ── C#-side UV2 assignment ──
                Vector2[] uv2;
                uint[] vertChartId;
                int conflicts;
                AssignUv2(vertCount, faceCount, tris,
                          outVertCount, outXref, outUV, outChart,
                          outIndexCount, outIdx,
                          out uv2, out vertChartId, out conflicts);

                result.conflictVertices = conflicts;

                LogPostPackDensity(uv2, tris, positions, shells, mesh.name + " [postAssign]");

                // ── Post-process: fix orphan vertices ──
                int orphanVerts, orphanTris, snapped;
                FixOrphanVertices(uv2, tris, vertChartId, out orphanVerts, out orphanTris, out snapped);
                result.orphanVertices = orphanVerts;
                result.orphanTriangles = orphanTris;
                result.snappedVertices = snapped;

                LogPostPackDensity(uv2, tris, positions, shells, mesh.name + " [postOrphan]");

                // ── Diagnostic: top longest UV2 edges (after fix) ──
                DiagnoseLongestEdges(uv2, tris, faceShellIds, vertChartId, 10);

                if (opts.postPackDensityCorrection)
                {
                    ApplyPostPackDensityCorrection(uv2, tris, positions, shells, mesh.name);
                    LogPostPackDensity(uv2, tris, positions, shells, mesh.name + " [postCorrection]");
                }

                // ── Border padding inset ──
                if (opts.borderPadding > 0 && result.atlasWidth > 0)
                {
                    // Inset is computed against user-facing resolution, not the
                    // oversampled internal atlas dims — otherwise border pixels
                    // become sub-pixel in the final lightmap.
                    uint refAtlasW = opts.resolution > 0 ? opts.resolution : result.atlasWidth;
                    uint refAtlasH = opts.resolution > 0 ? opts.resolution : result.atlasHeight;
                    ApplyBorderInset(uv2, opts.borderPadding, refAtlasW, refAtlasH);
                    LogPostPackDensity(uv2, tris, positions, shells, mesh.name + " [postBorder]");
                }

                // ── Apply UV2 (channel 1 — Unity lightmap channel, mesh.uv2) ──
                int clampedOutOfUnit = 0;
                if (opts.clampLightmapToUnit)
                    clampedOutOfUnit = ClampUvsToUnit(uv2);
                mesh.SetUVs(1, uv2);
                if (clampedOutOfUnit > 0)
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"Clamped {clampedOutOfUnit} UV2 vert(s) into [0,1]");
                result.ok = true;

                double coverage = ComputeUv2CoverageFraction(uv2, tris);
                UvtLog.Info(UvtLog.Category.Repack,
                    $"Atlas utilization: {coverage * 100.0:F1}% of [0,1]² covered ({shells.Count} shells)");

                LogPostPackDensity(uv2, tris, positions, shells, mesh.name + " [final]");

                // ── Stats ──
                int nonZero = 0;
                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;
                for (int i = 0; i < vertCount; i++)
                {
                    if (uv2[i].sqrMagnitude > 1e-12f)
                    {
                        nonZero++;
                        if (uv2[i].x < minU) minU = uv2[i].x;
                        if (uv2[i].x > maxU) maxU = uv2[i].x;
                        if (uv2[i].y < minV) minV = uv2[i].y;
                        if (uv2[i].y > maxV) maxV = uv2[i].y;
                    }
                }

                UvtLog.Verbose($"[xatlas] '{mesh.name}': atlas={result.atlasWidth}x{result.atlasHeight}, " +
                          $"charts={result.chartCount}, conflicts={conflicts}, orphans={orphanVerts}");
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // Repack multiple meshes into a single shared atlas.
        // All meshes are added to one xatlas session so their UV2 islands
        // are packed together without overlapping.
        // ─────────────────────────────────────────────────────────────────
        public static RepackResult[] RepackMulti(Mesh[] meshes, RepackOptions opts)
        {
            int meshCount = meshes.Length;
            var results = new RepackResult[meshCount];

            // ── Per-mesh pre-processing data ──
            var allUv0        = new Vector2[meshCount][];
            var allTris       = new int[meshCount][];
            var allPositions  = new Vector3[meshCount][];
            var allShells     = new List<UvShell>[meshCount];
            var allOverlap    = new List<List<int>>[meshCount];
            var allFaceShells = new uint[meshCount][];

            // Validate all meshes up-front
            for (int m = 0; m < meshCount; m++)
            {
                var mesh = meshes[m];
                allUv0[m] = mesh.uv;
                if (allUv0[m] == null || allUv0[m].Length == 0)
                {
                    results[m].error = "Mesh has no UV0";
                    return results;
                }
                allTris[m] = mesh.triangles;
                allPositions[m] = mesh.vertices;
                List<UvShell> shells;
                List<List<int>> overlapGroups;
                allFaceShells[m] = UvShellExtractor.BuildPerFaceShellIds(
                    allUv0[m], allTris[m], out shells, out overlapGroups);
                allShells[m]  = shells;
                allOverlap[m] = overlapGroups;

                results[m].shellCount        = shells.Count;
                results[m].overlapGroupCount  = overlapGroups.Count;
                int overlapPairs = UvShellExtractor.CountAabbOverlaps(shells);
                UvtLog.Verbose($"[xatlas] Pre-repack mesh {m}: {shells.Count} shells, " +
                    $"{overlapGroups.Count} overlap groups, {overlapPairs} overlapping pairs");

                LogHardEdgeAnalysis(shells, allTris[m], allPositions[m], meshLabel: mesh.name);
            }

            // UV0 winding normalized by ExecWeldUv0.
            for (int m = 0; m < meshCount; m++)
                results[m].flippedShells = 0;

            // Local UV0 copies (flattened) per mesh — fed to xatlas, mutated
            // by pre-pack passes (ARAP + density normalisation + perturbation);
            // mesh.uv is never touched.
            var allUvFlat = new float[meshCount][];

            // ── Single xatlas session for all meshes ──
            XatlasNative.xatlasCreate();
            try
            {
                // Add all meshes
                for (int m = 0; m < meshCount; m++)
                {
                    var mesh = meshes[m];
                    int vertCount = mesh.vertexCount;
                    int faceCount = allTris[m].Length / 3;

                    float[] uvFlat = new float[vertCount * 2];
                    for (int i = 0; i < vertCount; i++)
                    {
                        uvFlat[i * 2]     = allUv0[m][i].x;
                        uvFlat[i * 2 + 1] = allUv0[m][i].y;
                    }
                    allUvFlat[m] = uvFlat;

                    // Pre-pack pipeline (same two stages as RepackSingle):
                    //   1. ARAP on stretched shells (Sander L²).
                    //   2. Texel density (per-shell uniform scale).
                    if (opts.reparameterizeStretchedShells)
                    {
                        int stretchedFoundM = 0, convergedM = 0, skippedM = 0;
                        for (int si = 0; si < allShells[m].Count; si++)
                        {
                            var shell = allShells[m][si];
                            if (shell?.vertexIndices == null || shell.vertexIndices.Count < 3) continue;
                            float l2 = ShellQuality.ComputeL2Stretch(shell, allTris[m], allPositions[m], uvFlat);
                            if (float.IsNaN(l2) || l2 < opts.stretchThreshold) continue;
                            stretchedFoundM++;
                            var shellTriIndices = shell.faceIndices?.ToArray() ?? new int[0];
                            if (shellTriIndices.Length == 0) { skippedM++; continue; }
                            if (ArapParameterization.Reparameterize(
                                    allPositions[m], allTris[m], shellTriIndices, shell.vertexIndices,
                                    uvFlat, opts.arapIterations, out int _initFlippedM))
                            {
                                convergedM++;
                                UvtLog.Verbose(UvtLog.Category.Repack,
                                    $"[Repack] ARAP mesh {m}: shell {si} L²={l2:F2} (>{opts.stretchThreshold:F2}) → reparameterized");
                            }
                            else
                                skippedM++;
                        }
                        if (stretchedFoundM > 0)
                            UvtLog.Info(UvtLog.Category.Repack,
                                $"[Repack] ARAP mesh {m}: reparameterized {convergedM}/{stretchedFoundM} stretched shells (L²>{opts.stretchThreshold:F2}, skipped {skippedM})");
                    }

                    if (opts.normalizeTexelDensity)
                    {
                        // Normalize logs its own [Density] summary at Info level.
                        TexelDensityNormalizer.Normalize(
                            uvFlat, allShells[m], allTris[m], allPositions[m],
                            targetCoverage: opts.targetUvCoverage);
                    }

                    // Snap each shell extent to integer atlas-pixel grid so
                    // xatlas's per-chart ceil(extents * tpu) becomes a no-op.
                    // Uses the same effectiveTpu the joint-pack call below
                    // passes to xatlas — otherwise the snap grid wouldn't
                    // line up with the actual pack grid.
                    if (opts.snapShellsToIntegerPixels)
                    {
                        int oversamplePreM = opts.internalOversample > 0 ? opts.internalOversample : 1;
                        uint internalResMSnap = opts.resolution * (uint)oversamplePreM;
                        float tpuMSnap = ComputeEffectiveTpu(opts, internalResMSnap);
                        if (tpuMSnap > 0f)
                            SnapShellsToIntegerPixels(uvFlat, allShells[m], tpuMSnap, meshes[m]?.name ?? $"mesh#{m}");
                    }

                    // Perturb UV0 of overlap-grouped shells so xatlas treats
                    // every tile-instance as a unique chart (lightmap UV2 must
                    // be unique per shell; xatlas otherwise dedups identical
                    // input UVs into the same atlas slot). Strength is either
                    // user-supplied or computed adaptively from atlas
                    // resolution + padding.
                    float perturbStrengthM = opts.perturbStrength > 0f
                        ? opts.perturbStrength
                        : ComputeAdaptivePerturbStrength(opts.resolution, opts.padding);
                    PerturbOverlapShellsUv0(uvFlat, allShells[m], allOverlap[m], perturbStrengthM);

                    // Every shell is fed to xatlas as its own chart (UV2 is
                    // a unique-per-shell channel; the deleted "merge overlap
                    // tiles" mode produced shared lightmap regions for tile
                    // instances which is incorrect bake output).
                    uint[] indices = new uint[allTris[m].Length];
                    for (int i = 0; i < allTris[m].Length; i++)
                        indices[i] = (uint)allTris[m][i];
                    uint[] xatlasFaceShellIds = allFaceShells[m];
                    uint   xatlasFaceCount    = (uint)faceCount;

                    int addErr = XatlasNative.xatlasAddUvMesh(
                        uvFlat, (uint)vertCount,
                        indices, (uint)indices.Length,
                        xatlasFaceShellIds, xatlasFaceCount);

                    if (addErr != 0)
                    {
                        results[m].error = $"xatlasAddUvMesh error {addErr}";
                        return results;
                    }
                }

                // Pack all charts together into one atlas
                // See RepackSingle for oversample rationale and ComputeEffectiveTpu
                // for why we pass an explicit tpu instead of letting xatlas
                // compute its own (must match the snap grid above).
                int oversampleM = opts.internalOversample > 0 ? opts.internalOversample : 1;
                uint internalResM = opts.resolution * (uint)oversampleM;
                uint internalPadM = opts.padding    * (uint)oversampleM;
                float effectiveTpuM = ComputeEffectiveTpu(opts, internalResM);

                int totalShellsM = 0;
                for (int m = 0; m < meshCount; m++)
                    if (allShells[m] != null) totalShellsM += allShells[m].Count;

                bool packedM = RunPackCancelable(
                    "MultiMesh", totalShellsM, internalResM,
                    opts.maxChartSize, internalPadM, effectiveTpuM, internalResM,
                    opts.bilinear  ? 1 : 0,
                    opts.blockAlign ? 1 : 0,
                    opts.bruteForce ? 1 : 0,
                    opts.rotateCharts ? 1 : 0,
                    opts.rotateChartsToAxis ? 1 : 0);
                if (!packedM)
                {
                    for (int m = 0; m < meshCount; m++)
                        results[m].error = "cancelled";
                    return results;
                }

                int outMeshCount = XatlasNative.xatlasGetMeshCount();
                if (outMeshCount == 0)
                {
                    for (int m = 0; m < meshCount; m++)
                        results[m].error = "xatlas returned 0 meshes";
                    return results;
                }

                uint atlasW = XatlasNative.xatlasGetAtlasWidth();
                uint atlasH = XatlasNative.xatlasGetAtlasHeight();
                uint totalCharts = XatlasNative.xatlasGetChartCount();

                    UvtLog.Info($"[xatlas] Joint atlas: {atlasW}x{atlasH}, total_charts={totalCharts}, meshes={outMeshCount}");

                // ── Per-mesh output extraction ──
                var allUv2 = new Vector2[meshCount][];

                for (int m = 0; m < meshCount; m++)
                {
                    var mesh = meshes[m];
                    int vertCount = mesh.vertexCount;
                    int faceCount = allTris[m].Length / 3;

                    results[m].atlasWidth  = atlasW;
                    results[m].atlasHeight = atlasH;

                    int outVertCount  = XatlasNative.xatlasGetOutputVertexCount(m);
                    int outIndexCount = XatlasNative.xatlasGetOutputIndexCount(m);

                    if (outVertCount == 0 || outIndexCount == 0)
                    {
                        results[m].error = $"xatlas output empty for mesh {m}: verts={outVertCount}, idx={outIndexCount}";
                        continue;
                    }

                    uint[]  outXref  = new uint[outVertCount];
                    float[] outUV    = new float[outVertCount * 2];
                    uint[]  outChart = new uint[outVertCount];
                    uint[]  outIdx   = new uint[outIndexCount];

                    XatlasNative.xatlasGetOutputVertexData(m, outXref, outUV, outChart, outVertCount);
                    XatlasNative.xatlasGetOutputIndices(m, outIdx, outIndexCount);

                    LogRawXatlasDensity(outUV, outChart, outIdx, outVertCount, outIndexCount, meshes[m].name);

                    results[m].chartCount = (uint)outVertCount; // per-mesh chart count approximation

                    // Assign UV2
                    Vector2[] uv2;
                    uint[] vertChartId;
                    int conflicts;
                    AssignUv2(vertCount, faceCount, allTris[m],
                              outVertCount, outXref, outUV, outChart,
                              outIndexCount, outIdx,
                              out uv2, out vertChartId, out conflicts);
                    results[m].conflictVertices = conflicts;

                    LogPostPackDensity(uv2, allTris[m], allPositions[m], allShells[m], meshes[m].name + " [postAssign]");

                    // Fix orphan vertices
                    int orphanVerts, orphanTris, snapped;
                    FixOrphanVertices(uv2, allTris[m], vertChartId, out orphanVerts, out orphanTris, out snapped);
                    results[m].orphanVertices  = orphanVerts;
                    results[m].orphanTriangles = orphanTris;
                    results[m].snappedVertices = snapped;

                    LogPostPackDensity(uv2, allTris[m], allPositions[m], allShells[m], meshes[m].name + " [postOrphan]");

                    if (opts.postPackDensityCorrection)
                    {
                        ApplyPostPackDensityCorrection(uv2, allTris[m], allPositions[m], allShells[m], meshes[m].name);
                        LogPostPackDensity(uv2, allTris[m], allPositions[m], allShells[m], meshes[m].name + " [postCorrection]");
                    }

                    allUv2[m] = uv2;
                    results[m].ok = true;

                    double coverageM = ComputeUv2CoverageFraction(uv2, allTris[m]);
                    UvtLog.Info(UvtLog.Category.Repack,
                        $"Atlas utilization mesh {m}: {coverageM * 100.0:F1}% of [0,1]² covered ({allShells[m].Count} shells)");
                }

                // Apply UV2, border padding, and atlas-fill normalization
                int clampedTotal = 0;
                for (int m = 0; m < meshCount; m++)
                {
                    if (allUv2[m] == null || !results[m].ok) continue;

                    if (opts.borderPadding > 0 && atlasW > 0)
                    {
                        // Inset against user-facing resolution, not oversampled atlas.
                        uint refAtlasW = opts.resolution > 0 ? opts.resolution : atlasW;
                        uint refAtlasH = opts.resolution > 0 ? opts.resolution : atlasH;
                        ApplyBorderInset(allUv2[m], opts.borderPadding, refAtlasW, refAtlasH);
                        LogPostPackDensity(allUv2[m], allTris[m], allPositions[m], allShells[m], meshes[m].name + " [postBorder]");
                    }

                    if (opts.clampLightmapToUnit)
                        clampedTotal += ClampUvsToUnit(allUv2[m]);

                    meshes[m].SetUVs(1, allUv2[m]);

                    LogPostPackDensity(allUv2[m], allTris[m], allPositions[m], allShells[m], meshes[m].name + " [final]");
                }
                if (clampedTotal > 0)
                    UvtLog.Verbose(UvtLog.Category.Repack,
                        $"Clamped {clampedTotal} UV2 vert(s) into [0,1] across {meshCount} mesh(es)");
            }
            finally
            {
                XatlasNative.xatlasDestroy();
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────────
        // Apply border inset: shrink all UV2 toward center to leave
        // borderPadding pixels of margin at atlas edges.
        // uv2 = uv2 * (1 - 2*inset) + inset
        // ─────────────────────────────────────────────────────────────────
        public static void ApplyBorderInset(Mesh mesh, int borderPaddingPx, uint atlasSize)
        {
            if (borderPaddingPx <= 0 || atlasSize == 0) return;

            var uv2List = new List<Vector2>();
            mesh.GetUVs(1, uv2List);
            if (uv2List.Count == 0) return;

            float inset = (float)borderPaddingPx / atlasSize;
            float scale = 1f - 2f * inset;

            if (scale <= 0f)
            {
                UvtLog.Warn($"[xatlas] Border padding {borderPaddingPx}px too large " +
                                 $"for atlas {atlasSize}px — skipping inset.");
                return;
            }

            var uv2 = uv2List.ToArray();
            for (int i = 0; i < uv2.Length; i++)
                uv2[i] = uv2[i] * scale + new Vector2(inset, inset);

            mesh.SetUVs(1, uv2);
        }

        public static Vector2[] ApplyBorderInset(Vector2[] uv2, int borderPaddingPx, uint atlasSize)
        {
            if (borderPaddingPx <= 0 || atlasSize == 0 || uv2 == null) return uv2;

            float inset = (float)borderPaddingPx / atlasSize;
            float scale = 1f - 2f * inset;
            if (scale <= 0f) return uv2;

            var result = new Vector2[uv2.Length];
            for (int i = 0; i < uv2.Length; i++)
                result[i] = uv2[i] * scale + new Vector2(inset, inset);
            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // Fix orphan vertices: xatlas assigned chartIndex=0xFFFFFFFF to
        // vertices it couldn't place in any chart. These vertices get
        // near-zero UV2, creating diagonal stretches across the atlas.
        //
        // For each triangle containing an orphan vertex:
        //   - If 1 orphan: snap it to midpoint of the other 2 (valid) verts
        //   - If 2 orphans: snap both to the 1 valid vert
        //   - If 3 orphans: collapse to centroid (all near-zero anyway)
        //
        // Only snap if vertex is used in MORE orphan-tris than valid-tris,
        // to avoid breaking vertices that are mostly correct.
        // ─────────────────────────────────────────────────────────────────
        static void FixOrphanVertices(
            Vector2[] uv2, int[] tris, uint[] vertChartId,
            out int orphanVertCount, out int orphanTriCount, out int snappedCount)
        {
            orphanVertCount = 0;
            orphanTriCount = 0;
            snappedCount = 0;

            int vertCount = uv2.Length;
            int faceCount = tris.Length / 3;

            // Count orphan vertices
            bool[] isOrphan = new bool[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                if (vertChartId[v] == ORPHAN_CHART)
                {
                    isOrphan[v] = true;
                    orphanVertCount++;
                }
            }

            if (orphanVertCount == 0)
                return;

            // Find triangles with orphan vertices, track per-vertex usage
            var orphanFaces = new List<int>();
            int[] orphanTriUse = new int[vertCount]; // how many orphan-tris use this vert
            int[] validTriUse  = new int[vertCount]; // how many valid-tris use this vert

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                bool o0 = isOrphan[i0], o1 = isOrphan[i1], o2 = isOrphan[i2];

                if (o0 || o1 || o2)
                {
                    orphanFaces.Add(f);
                    orphanTriUse[i0]++; orphanTriUse[i1]++; orphanTriUse[i2]++;
                }
                else
                {
                    validTriUse[i0]++; validTriUse[i1]++; validTriUse[i2]++;
                }
            }

            orphanTriCount = orphanFaces.Count;

            // Snap orphan vertices
            // Collect proposed snap targets (there may be multiple per vertex from different faces)
            var snapTargets = new Dictionary<int, List<Vector2>>();

            foreach (int f in orphanFaces)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                bool o0 = isOrphan[i0], o1 = isOrphan[i1], o2 = isOrphan[i2];

                int orphans = (o0 ? 1 : 0) + (o1 ? 1 : 0) + (o2 ? 1 : 0);

                if (orphans == 1)
                {
                    // 1 orphan → snap to midpoint of 2 valid
                    int ov = o0 ? i0 : (o1 ? i1 : i2);
                    Vector2 anchor;
                    if (o0) anchor = (uv2[i1] + uv2[i2]) * 0.5f;
                    else if (o1) anchor = (uv2[i0] + uv2[i2]) * 0.5f;
                    else anchor = (uv2[i0] + uv2[i1]) * 0.5f;

                    AddSnapTarget(snapTargets, ov, anchor);
                }
                else if (orphans == 2)
                {
                    // 2 orphans → snap both to the 1 valid vertex
                    if (!o0) { AddSnapTarget(snapTargets, i1, uv2[i0]); AddSnapTarget(snapTargets, i2, uv2[i0]); }
                    else if (!o1) { AddSnapTarget(snapTargets, i0, uv2[i1]); AddSnapTarget(snapTargets, i2, uv2[i1]); }
                    else { AddSnapTarget(snapTargets, i0, uv2[i2]); AddSnapTarget(snapTargets, i1, uv2[i2]); }
                }
                else // 3 orphans
                {
                    Vector2 centroid = (uv2[i0] + uv2[i1] + uv2[i2]) / 3f;
                    AddSnapTarget(snapTargets, i0, centroid);
                    AddSnapTarget(snapTargets, i1, centroid);
                    AddSnapTarget(snapTargets, i2, centroid);
                }
            }

            // Apply snaps: average all proposed targets for each vertex
            foreach (var kv in snapTargets)
            {
                int v = kv.Key;

                // Only snap if vertex appears more in orphan tris than valid tris
                if (orphanTriUse[v] < validTriUse[v])
                    continue;

                var targets = kv.Value;
                Vector2 avg = Vector2.zero;
                for (int i = 0; i < targets.Count; i++)
                    avg += targets[i];
                avg /= targets.Count;

                uv2[v] = avg;
                snappedCount++;
            }

            UvtLog.Verbose($"[xatlas] Post-process: snapped {snappedCount}/{orphanVertCount} orphan vertices");
        }

        static void AddSnapTarget(Dictionary<int, List<Vector2>> dict, int vertIdx, Vector2 target)
        {
            if (!dict.TryGetValue(vertIdx, out var list))
            {
                list = new List<Vector2>(4);
                dict[vertIdx] = list;
            }
            list.Add(target);
        }

        // ─────────────────────────────────────────────────────────────────
        // Diagnostic: top longest UV2 edges
        // ─────────────────────────────────────────────────────────────────
        struct EdgeInfo
        {
            public int face;
            public int v0, v1;
            public uint shell;
            public uint chart0, chart1;
            public Vector2 uv2_0, uv2_1;
            public float length;
        }

        static void DiagnoseLongestEdges(Vector2[] uv2, int[] tris, uint[] faceShellIds,
                                          uint[] vertChartId, int topN)
        {
            int faceCount = tris.Length / 3;
            var longest = new List<EdgeInfo>(topN + 1);
            float minKeep = 0f;

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                uint shell = faceShellIds[f];
                CheckEdge(longest, ref minKeep, topN, f, i0, i1, shell, uv2, vertChartId);
                CheckEdge(longest, ref minKeep, topN, f, i1, i2, shell, uv2, vertChartId);
                CheckEdge(longest, ref minKeep, topN, f, i2, i0, shell, uv2, vertChartId);
            }

            longest.Sort((a, b) => b.length.CompareTo(a.length));
        }

        static void CheckEdge(List<EdgeInfo> list, ref float minKeep, int topN,
                               int face, int v0, int v1, uint shell,
                               Vector2[] uv2, uint[] vertChartId)
        {
            float len = (uv2[v0] - uv2[v1]).magnitude;
            if (len <= minKeep && list.Count >= topN) return;

            list.Add(new EdgeInfo
            {
                face = face, v0 = v0, v1 = v1, shell = shell,
                chart0 = vertChartId != null && v0 < vertChartId.Length ? vertChartId[v0] : ORPHAN_CHART,
                chart1 = vertChartId != null && v1 < vertChartId.Length ? vertChartId[v1] : ORPHAN_CHART,
                uv2_0 = uv2[v0], uv2_1 = uv2[v1], length = len,
            });

            if (list.Count > topN)
            {
                list.Sort((a, b) => b.length.CompareTo(a.length));
                list.RemoveAt(list.Count - 1);
                minKeep = list[list.Count - 1].length;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Border padding inset: shrink UV layout away from atlas edges
        // uv = uv * (1 - 2*inset) + inset  where inset = borderPx / atlasSize
        // ─────────────────────────────────────────────────────────────────
        /// <summary>
        /// Clamps every UV2 coord into [0,1] in place. Returns the number of
        /// vertices that had at least one axis outside the unit square (only
        /// counts vertices, not axes, so a vert with both axes out is still 1).
        /// </summary>
        internal static int ClampUvsToUnit(Vector2[] uv2)
        {
            if (uv2 == null) return 0;
            int n = 0;
            for (int i = 0; i < uv2.Length; i++)
            {
                Vector2 v = uv2[i];
                bool outside = v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f;
                if (outside)
                {
                    uv2[i] = new Vector2(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y));
                    n++;
                }
            }
            return n;
        }

        static void ApplyBorderInset(Vector2[] uv2, uint borderPx, uint atlasW, uint atlasH)
        {
            float insetX = (float)borderPx / atlasW;
            float insetY = (float)borderPx / atlasH;
            float scaleX = 1f - 2f * insetX;
            float scaleY = 1f - 2f * insetY;

            if (scaleX <= 0f || scaleY <= 0f)
            {
                UvtLog.Warn($"[xatlas] Border padding {borderPx}px too large for atlas {atlasW}x{atlasH}");
                return;
            }

            for (int i = 0; i < uv2.Length; i++)
            {
                uv2[i] = new Vector2(
                    uv2[i].x * scaleX + insetX,
                    uv2[i].y * scaleY + insetY);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // UV2 assignment: majority vote per original vertex
        // ─────────────────────────────────────────────────────────────────
        static void AssignUv2(
            int vertCount, int faceCount, int[] tris,
            int outVertCount, uint[] outXref, float[] outUV, uint[] outChart,
            int outIndexCount, uint[] outIdx,
            out Vector2[] uv2, out uint[] vertChartId, out int conflictCount)
        {
            uv2 = new Vector2[vertCount];
            vertChartId = new uint[vertCount];
            conflictCount = 0;

            for (int i = 0; i < vertCount; i++)
                vertChartId[i] = ORPHAN_CHART;

            var vertEntries = new List<ChartUv2Entry>[vertCount];

            for (int i = 0; i < outVertCount; i++)
            {
                uint orig = outXref[i];
                if (orig >= (uint)vertCount) continue;

                var entry = new ChartUv2Entry
                {
                    chartId = outChart[i],
                    uv = new Vector2(outUV[i * 2], outUV[i * 2 + 1]),
                    triCount = 0
                };

                if (vertEntries[orig] == null)
                    vertEntries[orig] = new List<ChartUv2Entry>(2);

                bool found = false;
                var list = vertEntries[orig];
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].chartId == entry.chartId)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    list.Add(entry);
            }

            int outFaceCount = outIndexCount / 3;
            for (int f = 0; f < outFaceCount; f++)
            {
                uint chart = outChart[outIdx[f * 3]];
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 0]], chart);
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 1]], chart);
                IncrementChartTriCount(vertEntries, outXref[outIdx[f * 3 + 2]], chart);
            }

            for (int v = 0; v < vertCount; v++)
            {
                var list = vertEntries[v];
                if (list == null || list.Count == 0) continue;

                if (list.Count == 1)
                {
                    uv2[v] = list[0].uv;
                    vertChartId[v] = list[0].chartId;
                    continue;
                }

                conflictCount++;
                int bestIdx = 0;
                int bestCount = list[0].triCount;
                for (int j = 1; j < list.Count; j++)
                {
                    if (list[j].triCount > bestCount)
                    {
                        bestCount = list[j].triCount;
                        bestIdx = j;
                    }
                }
                uv2[v] = list[bestIdx].uv;
                vertChartId[v] = list[bestIdx].chartId;
            }
        }

        struct ChartUv2Entry
        {
            public uint chartId;
            public Vector2 uv;
            public int triCount;
        }

        static void IncrementChartTriCount(List<ChartUv2Entry>[] entries, uint origVert, uint chart)
        {
            if (origVert >= (uint)entries.Length) return;
            var list = entries[origVert];
            if (list == null) return;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].chartId == chart)
                {
                    var e = list[j]; e.triCount++; list[j] = e;
                    return;
                }
            }
        }
    }
}
