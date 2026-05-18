// HierarchicalRepack.cs — builder for the inverse-hierarchical UV2 atlas.
//
// Phase A of the new pipeline. Read-only — does NOT write mesh.uv2 yet
// (that's InverseTransfer in PR-3). Just produces the per-mesh-per-face
// → LightingDomain assignment + the atlas layout that InverseTransfer
// will project into.
//
// Pipeline (PR-2.7 — Frostbite-style per-vertex projection):
//   1. Pick the deepest LOD from the LODGroup as the "proxy" — its shells
//      define the lighting domains that finer LODs will share.
//   2. Extract 3D shells on the deepest LOD via union-find on face
//      adjacency + normal threshold (≤30° — matches probe v2/v3 + xatlas
//      hard-edge analysis convention). Adjacency uses CANONICAL vertex
//      indices (deduplicated by world-space position) so UV/normal seams
//      in Unity's mesh.vertices don't fragment a single physical surface
//      into many single-tri shells. Degenerate (area<1e-12) tris are
//      skipped entirely (faceToDomain = -1).
//   3. For each finer LOD: project every vertex onto the closest deepest-
//      LOD triangle (brute-force scan + per-tri AABB rejection). Per fine
//      face:
//        Overlay  — all three corners are within overlayDistNorm × meshDiag
//                   of the proxy AND collapse to the same proxy shell:
//                   the face inherits that shell's atlas rect. No new
//                   domain — wall+sign, box+decal, roof+ornament.
//        Marked   — anything else (any corner detached from proxy, OR
//                   corners straddle shell boundaries → face has no
//                   single parent and would interpolate across atlas
//                   regions). Goes into the "needs own domain" pile.
//   4. Cluster Marked faces by adjacency (union-find again) per fine LOD.
//      Each cluster classifies as:
//        Skip     — tiny + few faces (skipAreaFrac × deep area AND
//                   ≤ skipMaxFaceCount). Handles, fasteners, noise.
//        Promote  — gets its own atlas domain.
//   5. Build LightingDomain[]: one per base shell + one per promoted
//      cluster. Overlaid/skipped faces don't create domains.
//   6. Pack atlas rects. PR-2 used a naive horizontal-strip packer; this
//      stage keeps it as a placeholder until PR-2.8 swaps xatlas via
//      XatlasNative.
//
// Why per-vertex projection vs the PR-2.5 per-shell angle/extent test:
//   • Robust to fragmented base shells (Carousel cylinder: 270 small
//     shells stop being a problem because a fine vertex finds the
//     closest one regardless of how many parents exist).
//   • Robust to lost angled panels after dedup (Gazebo octagonal roof:
//     fine vertex projects onto the merged Y-axis roof at a finite
//     distance, overlays cleanly).
//   • A single physically-meaningful threshold (distance) replaces three
//     correlated geometric tests (angle, perp distance, planar fit).
//   • Same complexity O(fine verts × deep faces) with AABB prefilter —
//     well under a second on our worst test case.
//
// Public types are documented for cross-PR clarity. Internal helpers
// duplicate small bits of HierarchicalDiag (shell extraction, face
// classification) to keep PR-2 a single file; refactor into a shared
// `HierarchicalShellExtractor` is a deliberate follow-up.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class HierarchicalRepack
    {
        // ─── Public configuration ────────────────────────────────────

        public struct Options
        {
            /// <summary>Adjacent-face normal threshold for shell extraction (degrees).
            /// Two adjacent faces belong to the same shell if their normals differ by
            /// less than this. Matches xatlas hard-edge convention.</summary>
            public float shellNormalThresholdDeg;

            /// <summary>Post-extraction merge pass: two adjacent SHELLS (sharing
            /// at least one canonical edge) get merged if their area-weighted
            /// dominant normals differ by less than this many degrees. Compensates
            /// for the per-face threshold being too strict at shell boundaries —
            /// a single noisy triangle on a planked surface can otherwise split
            /// what's physically one wall into two base shells, which then
            /// breaks face-consensus for any fine-LOD face that straddles the
            /// split. 0 = disabled (keep raw union-find output).</summary>
            public float shellMergeAngleDeg;

            /// <summary>Target atlas resolution (pixels). Final atlas may be slightly
            /// larger if shells don't fit; naive packer in PR-2 grows the height.</summary>
            public int atlasResolutionPx;

            /// <summary>Padding between domains in atlas pixels. Intra-domain overlap
            /// is intentional (shared lighting domain feature — see EXPERIMENTS.md);
            /// padding only applies BETWEEN domains, not within.</summary>
            public int interDomainPaddingPx;

            /// <summary>Per-vertex closest-surface-distance threshold (as a fraction
            /// of the deepest-LOD world-space mesh diagonal). A fine-LOD vertex
            /// whose distance to the deepest-LOD surface is ≤ this × meshDiag is
            /// considered "on the proxy" and inherits the proxy's UV via overlay.
            /// Replaces the PR-2.5 trio (overlayAngleDeg/PerpNorm/ExtentSlack) —
            /// per-vertex projection captures angle + offset + extent fit in one
            /// scalar, the way Frostbite's lightmap-proxy pipeline does.</summary>
            public float overlayDistNorm;

            /// <summary>Skip a fine-LOD promoted cluster if BOTH (a) its 3D area
            /// is below this fraction of the total deepest-LOD area AND (b) its
            /// face count is at or below <see cref="skipMaxFaceCount"/>. Handles,
            /// fasteners, and other small geometric noise where allocating any
            /// atlas space is wasteful.</summary>
            public float skipAreaFrac;

            /// <summary>Companion to <see cref="skipAreaFrac"/> — a cluster with
            /// many faces always promotes even if its total area is small,
            /// because face count alone implies someone will see it.</summary>
            public int skipMaxFaceCount;

            /// <summary>Which proxy UV2 variant drives the downstream
            /// per-shell projection. All three variants are still emitted
            /// as diagnostic PNGs (proxy_uv2_clean/raw/auto.png) regardless
            /// — this only picks which one Stage 2+ actually consumes.</summary>
            public ProxyMode proxyMode;

            /// <summary>Stage 2 Poisson sampling rate: samples per unit
            /// proxy area, scaled so the total sample count is
            /// invariant to mesh scale. Effective per-tri count is
            /// max(3, ceil(proxySampleDensity × tri_area /
            /// meshDiag²)). Default 4000 → ~4000 samples on a 1m² mesh,
            /// proportional otherwise. Higher = denser pattern at
            /// the cost of Stage 3 projection runtime.</summary>
            public int proxySampleDensity;

            public static Options Default => new Options
            {
                shellNormalThresholdDeg = 30f,
                shellMergeAngleDeg      = 15f,
                atlasResolutionPx       = 1024,
                interDomainPaddingPx    = 4,
                overlayDistNorm         = 0.03f,
                skipAreaFrac            = 0.001f,
                skipMaxFaceCount        = 4,
                proxyMode               = ProxyMode.Clean,
                proxySampleDensity      = 4000,
            };
        }

        /// <summary>Which UV2 layout drives Stage 2+ per-shell projection.</summary>
        public enum ProxyMode
        {
            /// <summary>UV0 → sym-split → ARAP → xatlas pack. Preserves
            /// artist UV chart partition (right answer for curved surfaces
            /// where each strip preserves uniform texel density along the
            /// arc); fixes mirrored overlaps and stretched islands.
            /// Production default.</summary>
            Clean = 0,
            /// <summary>UV0 → xatlas pack only. No sym-split, no ARAP.
            /// Diagnostic — shows what the cleanup steps contribute.</summary>
            Raw = 1,
            /// <summary>True auto-unwrap from positions + normals, no UV0
            /// input. xatlas builds charts from hard-edge detection — tends
            /// to pie-slice curved surfaces into single charts, which gives
            /// uneven texel density across the arc. Useful for assets with
            /// missing or unusable UV0; not recommended for production on
            /// curved geometry.</summary>
            Auto = 2,
        }

        /// <summary>
        /// One atlas region representing a single lighting domain — either a
        /// base shell from the deepest LOD (the typical case) or a promoted
        /// cluster of fine-LOD faces (detail that doesn't share its parent's
        /// lighting). The plane + basis (u,v unit vectors orthogonal to
        /// normal) are world-space; InverseTransfer projects a fine-LOD vertex
        /// onto this plane using basis dot products and maps the local 2D
        /// coord into <see cref="uv2Rect"/>.
        /// </summary>
        public struct LightingDomain
        {
            public Rect    uv2Rect;        // in [0,1]² atlas coords
            public Vector3 planeCentroid;  // world-space
            public Vector3 planeNormal;    // world-space, unit
            public Vector3 planeU;         // world-space basis u, unit, ⊥ normal
            public Vector3 planeV;         // world-space basis v, unit, ⊥ normal and u
            public float   planeExtentU;   // max signed projection along u (used to map local-2D → [0,1])
            public float   planeExtentV;   // same along v
            public bool    isPromoted;     // false = base shell on deepest LOD; true = promoted fine cluster
            public int     sourceLodIndex; // only meaningful when isPromoted: which fine LOD the cluster came from
            public int     faceCount;      // # faces contributing to this domain (diag)
            public float   totalArea3D;    // sum of contributing face areas in world space (diag)
        }

        public class Result
        {
            public LightingDomain[] domains;
            public int atlasPixelWidth;
            public int atlasPixelHeight;
            public int baseShellCount;
            public int promotedClusterCount;
            /// <summary>Per-LOD assignment: faceToDomain[lod][faceIdx] = domain index, or -1 if the face has no assignment (degenerate, skipped, multi-renderer LOD).</summary>
            public int[][] faceToDomain;
            public int    totalFineFaces;
            public int    promotedFineFaces;
            /// <summary>Fine faces whose shell was overlaid onto a parent base shell — they share a parent's atlas rect (PR-2.5).</summary>
            public int    overlaidFineFaces;
            /// <summary>Fine faces whose shell was discarded as geometric noise — no atlas slot, faceToDomain = -1 (PR-2.5).</summary>
            public int    skippedFineFaces;
            /// <summary>Degenerate (zero-area) faces filtered before shell extraction (PR-2.5).</summary>
            public int    degenerateFineFaces;
            /// <summary>PR-3 stage 1: clean UV2 layout for the deepest LOD,
            /// produced by xatlas auto-pack on its existing UV0. Diagnostic /
            /// reference for fine-LOD shell projection in subsequent stages.
            /// Null if the deepest LOD has no UV0 or xatlas couldn't pack.</summary>
            public Vector2[] proxyUv2;
            /// <summary>Companion to <see cref="proxyUv2"/> — the deepest LOD's
            /// triangle indices (mesh.triangles) so the UV2 array can be
            /// rendered as a UV layout PNG.</summary>
            public int[]     proxyTris;
            /// <summary>World-space positions of <see cref="proxyUv2"/>'s
            /// vertex set — indexed by <see cref="proxyTris"/>. Needed by
            /// Stage 2 sampling because sym-split (clean) or chart-seam
            /// splitting (auto) can rewrite the proxy's vertex layout vs
            /// the original deepest-LOD mesh.</summary>
            public Vector3[] proxyWorldVerts;
            /// <summary>Stage 1 diagnostic: clean variant (sym-split + ARAP
            /// + xatlas pack), always populated regardless of which mode
            /// <see cref="Options.proxyMode"/> picks for downstream use.</summary>
            public Vector2[] proxyUv2Clean;
            public int[]     proxyTrisClean;
            public Vector3[] proxyWorldVertsClean;
            /// <summary>Stage 1 comparison: raw repack of the deepest LOD's
            /// UV0 through xatlas (no sym-split, no ARAP). Shows what those
            /// steps contribute against the "clean" pipeline.</summary>
            public Vector2[] proxyUv2Raw;
            public int[]     proxyTrisRaw;
            public Vector3[] proxyWorldVertsRaw;
            /// <summary>Stage 1 comparison: TRUE auto-unwrap — xatlas builds
            /// charts from scratch using the deepest LOD's positions +
            /// normals (no UV0 hint). Requires the native bridge's
            /// xatlasAddMesh export.</summary>
            public Vector2[] proxyUv2Auto;
            public int[]     proxyTrisAuto;
            public Vector3[] proxyWorldVertsAuto;
            /// <summary>PR-3 Stage 2: Poisson-style samples drawn uniformly
            /// across the active proxy's 3D surface, each carrying the
            /// proxy UV2 it lands on. Drives Stage 3+ projection onto fine
            /// LOD shells. Null until Stage 2 runs.</summary>
            public ProxySample[] proxySamples;

            /// <summary>PR-3 Stage 3: per-fine-LOD projection of proxy
            /// samples onto the LOD's 3D surface. Indexed by LOD; entries
            /// for the deepest LOD or for LODs that don't have a renderer
            /// are null. Lets Stage 4 ask "which proxy shell dominates
            /// this fine face" and "how many samples hit this region" in
            /// O(1) per query.</summary>
            public FineLodProjection[] lodProjections;
            /// <summary>PR-3 Stage 5 output: final UV2 per vertex, per fine
            /// LOD. Indexed by LOD; entries for the deepest LOD or for
            /// LODs that didn't get a Stage 5 pass are null. Inner array
            /// length is the OUTPUT vertex count after seam-vertex
            /// duplication (vertices shared between fine faces that
            /// landed on different proxy faces get one copy per proxy
            /// face — keeps adjacent fine faces from dragging UVs
            /// across the atlas). Pair with <see cref="finalTris"/> and
            /// <see cref="finalSourceVertexIdx"/>.</summary>
            public Vector2[][] finalUv2;
            /// <summary>PR-3 Stage 5: rewritten triangle index buffer per
            /// fine LOD, indexing into the per-LOD finalUv2 / output
            /// vertex set (NOT the original mesh.triangles).</summary>
            public int[][]     finalTris;
            /// <summary>PR-3 Stage 5: for each output vertex, the index
            /// into the original mesh.vertices it was duplicated from.
            /// Stage 6 (mesh rebuild) will use this to copy positions,
            /// normals, tangents, UV0, etc. onto the duplicated mesh.
            /// </summary>
            public int[][]     finalSourceVertexIdx;
            /// <summary>PR-3 Stage 5: total V extent of the final atlas
            /// after promotion strip is appended. 1.0 if every fine shell
            /// overlaid cleanly; higher when shells needed promotion.
            /// </summary>
            public float finalAtlasV;
            public string error;
        }

        /// <summary>PR-3 Stage 3 output: for one fine LOD, how the proxy
        /// samples landed on its surface. Per-face aggregates feed Stage 4
        /// (per-shell affine fit, residual cluster) and the diagnostic
        /// heat-map (lod{N}_proxy_hits.png).</summary>
        public struct FineLodProjection
        {
            /// <summary>For each face: number of proxy samples whose
            /// closest-point-on-fine-mesh landed within the overlay
            /// distance threshold on this face.</summary>
            public int[]   perFaceHitCount;
            /// <summary>For each face: proxy shell that contributed the
            /// most hits, or -1 if no hits.</summary>
            public int[]   perFaceDominantProxyShell;
            /// <summary>For each face: mean closest-point distance across
            /// the samples that hit it (world units). 0 if no hits.</summary>
            public float[] perFaceAvgDist;
            /// <summary>Total samples that hit any face on this LOD.</summary>
            public int     totalHits;
            /// <summary>Samples whose closest-point distance exceeded the
            /// overlay threshold for this LOD — i.e. proxy regions with no
            /// counterpart on this fine LOD (rare; usually means proxy has
            /// extra geometry the fine LOD doesn't, or vice versa).</summary>
            public int     missedSamples;
            /// <summary>PR-3 Stage 3 bookkeeping: for each face, indices
            /// into <see cref="Result.proxySamples"/> of every sample that
            /// landed on it. Variable-length arrays (null if no hits).
            /// Currently only used to wire perFaceDominantProxyShell;
            /// retained because the diagnostic PNGs read it directly.</summary>
            public int[][] perFaceHitSampleIdx;
        }

        /// <summary>A Poisson-distributed point on the proxy surface paired
        /// with the metadata Stage 3+ needs to make per-shell decisions.
        /// Storing direction + chart + ID upfront avoids re-derivation in
        /// later stages and lets the projection trace carry richer signal
        /// (e.g. orientation-filtered shell vote, per-chart residual fit).</summary>
        public struct ProxySample
        {
            public Vector3 worldPos;
            /// <summary>Outward face normal of the proxy triangle this sample
            /// sits on, in world space. Stage 4 can reject candidate fine
            /// shells whose normal disagrees by &gt; threshold.</summary>
            public Vector3 worldNormal;
            public Vector2 uv2;
            /// <summary>Index of the proxy triangle this sample sits inside —
            /// lets Stage 3 group samples by proxy face / chart when needed.</summary>
            public int     proxyFaceIdx;
            /// <summary>Index of the proxy UV chart (shell) this sample's
            /// face belongs to. Two faces share a chart iff their UV2 is
            /// continuous across the shared edge — extracted via
            /// UvShellExtractor on the active proxy (proxyUv2 + proxyTris).</summary>
            public int     proxyShellId;
            /// <summary>Sequential ID assigned at sampling time. Stable
            /// because the LCG seed is fixed; lets later stages refer to
            /// specific samples (e.g. for debug picking).</summary>
            public int     sampleId;
        }

        // ─── Public entry ────────────────────────────────────────────

        /// <summary>
        /// Build the hierarchical atlas layout for a LODGroup. Returns a
        /// <see cref="Result"/> with the per-face domain assignment and the
        /// packed atlas dimensions. The actual UV2 write happens later in
        /// InverseTransfer (PR-3) — this stage decides "which domain does
        /// this face belong to" and "where is that domain in the atlas".
        ///
        /// Single-renderer-per-LOD only in PR-2. Multi-renderer support
        /// deferred to PR-3 or later.
        /// </summary>
        public static Result Build(LODGroup lg, Options opts)
        {
            var result = new Result();
            if (lg == null) { result.error = "LODGroup is null"; return result; }
            var lods = lg.GetLODs();
            if (lods == null || lods.Length < 2)
            {
                result.error = "LODGroup needs at least 2 LOD levels";
                return result;
            }

            // Pick a single renderer per LOD (first valid). Multi-renderer
            // LODs cause a warning but the pipeline still runs with the
            // first one — full multi-mesh support is a separate PR.
            int lodCount = lods.Length;
            var meshes = new Mesh[lodCount];
            var xforms = new Transform[lodCount];
            for (int li = 0; li < lodCount; li++)
            {
                var rs = lods[li].renderers;
                if (rs == null) continue;
                int found = 0;
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (found == 0) { meshes[li] = mf.sharedMesh; xforms[li] = r.transform; }
                    found++;
                }
                if (found > 1)
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] LOD{li}: {found} renderers — using first only (multi-mesh deferred).");
            }

            int deepest = lodCount - 1;
            while (deepest > 0 && meshes[deepest] == null) deepest--;
            if (deepest <= 0 || meshes[deepest] == null)
            {
                result.error = "No usable deepest-LOD mesh";
                return result;
            }
            if (meshes[0] == null)
            {
                result.error = "No usable LOD0 mesh";
                return result;
            }

            // ── Step 2: extract 3D shells on the deepest LOD ──
            float meshDiag = ComputeMeshDiagonal(meshes[deepest], xforms[deepest]);
            if (meshDiag < 1e-6f) meshDiag = 1f;
            int deepDegen;
            var deepFaces  = BuildFaceData(meshes[deepest], xforms[deepest], meshDiag,
                out Vector3[] deepWorldVerts, out int[] deepRawTris,
                out int[] deepCanonicalTris, out deepDegen);
            var deepFaceToShell = new int[deepFaces.Length];
            var deepShells = ExtractShells(deepFaces, deepWorldVerts, deepRawTris,
                deepCanonicalTris, opts.shellNormalThresholdDeg, opts.shellMergeAngleDeg,
                deepFaceToShell, null);
            float totalDeepArea = 0f;
            for (int si = 0; si < deepShells.Length; si++) totalDeepArea += deepShells[si].totalArea;
            if (totalDeepArea < 1e-12f) totalDeepArea = 1e-12f;

            // Precompute deepest-LOD per-tri AABBs for the projector's
            // early-out filter (built once, used by every fine-LOD vertex
            // query across all fine LODs).
            BuildDeepAabbs(deepWorldVerts, deepRawTris, out var deepMin, out var deepMax);
            float overlayDistAbs = opts.overlayDistNorm * meshDiag;

            // PR-3 Stage 1 (proxy UV2 generation): produce up to three
            // candidate UV2 layouts for the deepest LOD so the operator can
            // compare visually:
            //   • clean — UV0 → sym-split → xatlas pack (ARAP on by default)
            //   • raw   — UV0 → xatlas pack only (no sym-split, no ARAP).
            //             Diagnostic: shows what those steps contribute.
            //   • auto  — TRUE auto-unwrap: positions + normals → xatlas
            //             builds charts from scratch (no UV0 input). Needs
            //             the native bridge's xatlasAddMesh export.
            // All variants run on clones of the deepest mesh so the
            // operator's scene assets are untouched. The "clean" variant
            // is the one that will drive subsequent stages; the others are
            // diagnostic-only.
            try { ComputeProxyUv2Variants(meshes[deepest], xforms[deepest], opts, lg.name, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] proxy UV2 stage 1 failed on '{lg.name}': {ex.Message}");
            }
            // PR-3 Stage 2: Poisson samples on the active proxy.
            try { GenerateProxySamples(opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] proxy sampling stage 2 failed on '{lg.name}': {ex.Message}");
            }
            // PR-3 Stage 3: project samples onto each fine LOD.
            try { ProjectProxySamplesOntoFineLods(lg, opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] proxy projection stage 3 failed on '{lg.name}': {ex.Message}");
            }
            // PR-3 Stage 5: final UV2 per fine LOD via orthographic
            // projection of each fine face onto the nearest proxy face
            // (with normal-sign disambiguation for sym-split mirror
            // twins), then barycentric pull of proxy_uv2. Per the
            // operator's architecture: 'orthographically project each
            // finer LOD's faces into the LOD-deepest atlas regions by
            // 3D nearest-face + normal-sign correspondence.' No affine
            // fit, no clamp, no chart wrapping -- UV2 overlaps inside
            // a region are intentional (shared lighting domain).
            try { BuildFinalFineUv2(lg, opts, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] final uv2 stage 5 failed on '{lg.name}': {ex.Message}");
            }

            // ── Step 3: domain table init ──
            // Domain numbering:
            //   [0 .. baseN-1]              → base shells (deepest LOD)
            //   [baseN .. baseN+P-1]        → promoted fine shells (P grows)
            // Overlay assigns faceToDomain = parentBaseShellIdx.
            // Skip leaves faceToDomain = -1 (no atlas slot).
            int baseN = deepShells.Length;
            result.faceToDomain = new int[lodCount][];
            for (int li = 0; li < lodCount; li++)
            {
                int faceCount = (meshes[li] != null) ? meshes[li].triangles.Length / 3 : 0;
                var arr = new int[faceCount];
                for (int i = 0; i < faceCount; i++) arr[i] = -1;
                result.faceToDomain[li] = arr;
            }

            // Deepest LOD: each face → its own base shell (degenerate → -1).
            for (int f = 0; f < deepFaces.Length; f++)
                result.faceToDomain[deepest][f] = deepFaceToShell[f];

            // ── Step 4: per-vertex projection on each fine LOD ──
            // Project every fine-LOD vertex onto the deep mesh; then decide
            // each fine face based on its 3 corners' projection state:
            //   • All 3 verts within overlayDistAbs of the proxy AND all
            //     fall onto the SAME deep shell → Overlay(that shell).
            //   • Anything else → flagged for promote/skip clustering.
            // The mismatch case (corners straddle shell boundaries) goes to
            // promote because interpolating a fine face's UV across two
            // disjoint atlas rects would bleed lightmap data across
            // unrelated surfaces.
            var promotedClusters = new List<PromotedCluster>();
            int totalFineFaces = 0, promotedFineFaces = 0,
                overlaidFineFaces = 0, skippedFineFaces = 0, degenFineFaces = deepDegen;
            // degenFineFaces is a slight misnomer — it includes the deepest
            // LOD's degenerates too, so the report can show "all degenerate
            // tris dropped from atlas" in one number.

            for (int li = 0; li < deepest; li++)
            {
                if (meshes[li] == null) continue;
                int fineDegen;
                var fineFaces = BuildFaceData(meshes[li], xforms[li], meshDiag,
                    out Vector3[] fineWorldVerts, out int[] fineRawTris,
                    out int[] fineCanonicalTris, out fineDegen);
                degenFineFaces += fineDegen;
                totalFineFaces += fineFaces.Length;

                // Per-shell classification: extract fine shells first, then
                // vote each shell as a unit. A single outlier vertex no
                // longer drags a whole physical surface into promote — the
                // shell wins by majority of its vertices' nearest-base-shell
                // projections.
                //
                // Per-fine-vertex projection result: index into deepShells,
                // or -1 if the vertex is too far from the proxy surface.
                int vertCount = fineWorldVerts.Length;
                var vertOverlayShell = new int[vertCount];
                for (int v = 0; v < vertCount; v++)
                {
                    int closestFace = ProjectVertexToDeepMesh(fineWorldVerts[v],
                        deepFaces, deepWorldVerts, deepRawTris, deepMin, deepMax,
                        out float dist);
                    if (closestFace >= 0 && dist <= overlayDistAbs)
                        vertOverlayShell[v] = deepFaceToShell[closestFace];
                    else
                        vertOverlayShell[v] = -1;
                }

                // Extract fine shells (full mesh, no mask — every shell will
                // be classified). shellMergeAngleDeg = 0 here: fine shells
                // shouldn't merge across panel boundaries.
                var fineFaceToShell = new int[fineFaces.Length];
                var fineShells = ExtractShells(fineFaces, fineWorldVerts, fineRawTris,
                    fineCanonicalTris, opts.shellNormalThresholdDeg, 0f,
                    fineFaceToShell, null);

                // For each fine shell: vote on parent base shell across its
                // vertices. Winner = base shell with the most votes; if
                // winner gets ≥ overlayVoteFrac of total votes AND the shell
                // is not "tiny noise", overlay. Else skip (tiny) or promote.
                //
                // Vote counts are per-vertex (not per-face) so the shell
                // size dominates over face topology. A wall plank with 50
                // verts on LOD3 wall #5 and 2 verts straddling onto wall #6
                // overlays cleanly onto #5 instead of promoting.
                var shellVotes = new Dictionary<int, int>();
                var shellVertSet = new HashSet<int>();
                for (int s = 0; s < fineShells.Length; s++)
                {
                    var shell = fineShells[s];
                    shellVotes.Clear();
                    shellVertSet.Clear();
                    foreach (int f in shell.faceIndices)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = fineRawTris[f * 3 + k];
                            if (!shellVertSet.Add(vi)) continue;
                            int ps = vertOverlayShell[vi];
                            if (ps < 0) continue;
                            shellVotes.TryGetValue(ps, out int prev);
                            shellVotes[ps] = prev + 1;
                        }
                    }
                    int totalVotes = shellVertSet.Count;
                    int winnerShell = -1, winnerVotes = 0;
                    foreach (var kv in shellVotes)
                        if (kv.Value > winnerVotes) { winnerVotes = kv.Value; winnerShell = kv.Key; }

                    // Overlay threshold: winner takes a majority of the
                    // shell's vertices (≥ 50% by default — 1 outlier per
                    // 2 inliers is fine; a half-promoted shell is honest).
                    bool overlayPasses = winnerShell >= 0
                                      && totalVotes > 0
                                      && winnerVotes * 2 >= totalVotes;

                    float areaFrac = shell.totalArea / totalDeepArea;
                    bool tinyArea  = areaFrac < opts.skipAreaFrac;
                    bool fewFaces  = shell.faceCount <= opts.skipMaxFaceCount;

                    int domainIdx;
                    if (overlayPasses)
                    {
                        domainIdx = winnerShell;
                        overlaidFineFaces += shell.faceCount;
                    }
                    else if (tinyArea && fewFaces)
                    {
                        domainIdx = -1;
                        skippedFineFaces += shell.faceCount;
                    }
                    else
                    {
                        domainIdx = baseN + promotedClusters.Count;
                        promotedClusters.Add(MakePromotedClusterFromShell(shell, li));
                        promotedFineFaces += shell.faceCount;
                    }
                    foreach (int f in shell.faceIndices)
                        result.faceToDomain[li][f] = domainIdx;
                }
            }
            result.totalFineFaces      = totalFineFaces;
            result.promotedFineFaces   = promotedFineFaces;
            result.overlaidFineFaces   = overlaidFineFaces;
            result.skippedFineFaces    = skippedFineFaces;
            result.degenerateFineFaces = degenFineFaces;

            // ── Step 5: materialise LightingDomain[] ──
            var domains = new LightingDomain[baseN + promotedClusters.Count];
            for (int si = 0; si < baseN; si++)
            {
                domains[si] = MakeDomainFromShell(deepShells[si], isPromoted: false, sourceLodIndex: deepest);
            }
            for (int ci = 0; ci < promotedClusters.Count; ci++)
            {
                var c = promotedClusters[ci];
                domains[baseN + ci] = MakeDomainFromCluster(c);
            }

            // ── Step 6: naive horizontal-strip packer ──
            // Stack rectangles into a fixed-width atlas (opts.atlasResolutionPx),
            // wrapping to a new row when full. Each rect's pixel size derives
            // from its world-space planar extent normalized to a "texels per
            // world unit" target. PR-2.8 will replace this with xatlas via the
            // existing XatlasNative wrapper — at which point this method just
            // hands xatlas a virtual mesh with shellIDs and reads the rect
            // assignment back out.
            PackAtlasNaive(domains, opts, out int atlasW, out int atlasH);
            result.domains = domains;
            result.atlasPixelWidth = atlasW;
            result.atlasPixelHeight = atlasH;
            result.baseShellCount  = baseN;
            result.promotedClusterCount = promotedClusters.Count;
            return result;
        }

        // ─── Internal types ──────────────────────────────────────────

        struct Face3D
        {
            public Vector3 centroid;
            public Vector3 normal;     // unit
            public float   area;
        }

        struct Shell3D
        {
            public Vector3 centroid;       // area-weighted
            public Vector3 dominantNormal; // area-weighted, unit
            public float   totalArea;
            public int     faceCount;
            public List<int> faceIndices;  // into the source mesh's tris[] (faces, not verts)
            public Vector3 basisU;
            public Vector3 basisV;
            public float   extentU;        // half-extent along basisU (centred at centroid)
            public float   extentV;
        }

        sealed class PromotedCluster
        {
            public List<int> faceIndices = new List<int>();
            public Vector3   centroid;
            public Vector3   dominantNormal;
            public Vector3   basisU;
            public Vector3   basisV;
            public float     extentU;
            public float     extentV;
            public float     totalArea;
            public int       sourceLodIndex;
        }

        // ─── Face data + diagonal ────────────────────────────────────

        /// <summary>Build per-face data for one mesh. Out-params:
        /// <paramref name="worldVerts"/> = world-space copy of mesh.vertices
        /// (needed for vertex-based extent computation and per-vertex
        /// projection in the new classifier). <paramref name="rawTris"/> =
        /// mesh.triangles as-is (indexes worldVerts). <paramref name="canonicalTris"/>
        /// rewrites mesh.triangles using position-deduplicated vertex indices
        /// (epsilon = meshDiag × 1e-5) so adjacent triangles split by Unity
        /// UV/normal seams still share edges — without this, ExtractShells sees
        /// ~3× too many shells on curved geometry. <paramref name="degenerateCount"/>
        /// reports tris dropped (their Face3D.area is set to 0 so callers can
        /// skip them via faceToDomain = -1 instead of producing zero-area
        /// shells).</summary>
        static Face3D[] BuildFaceData(Mesh mesh, Transform xform, float meshDiag,
            out Vector3[] worldVerts, out int[] rawTris, out int[] canonicalTris,
            out int degenerateCount)
        {
            var localVerts = mesh.vertices;
            worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = xform.TransformPoint(localVerts[i]);
            rawTris = mesh.triangles;
            canonicalTris = BuildCanonicalIndices(worldVerts, rawTris, meshDiag);

            int n = rawTris.Length / 3;
            var data = new Face3D[n];
            int degenerate = 0;
            for (int f = 0; f < n; f++)
            {
                var a = worldVerts[rawTris[f * 3]];
                var b = worldVerts[rawTris[f * 3 + 1]];
                var c = worldVerts[rawTris[f * 3 + 2]];
                data[f].centroid = (a + b + c) / 3f;
                var cross = Vector3.Cross(b - a, c - a);
                float mag = cross.magnitude;
                if (mag > 1e-12f)
                {
                    data[f].normal = cross / mag;
                    data[f].area   = mag * 0.5f;
                }
                else
                {
                    data[f].normal = Vector3.up;
                    data[f].area   = 0f;
                    degenerate++;
                }
            }
            degenerateCount = degenerate;
            return data;
        }

        /// <summary>Quantize world-space vertex positions onto a grid of cell
        /// size (meshDiag × 1e-5) and return a rewritten triangle-index array
        /// where each corner references the canonical ID of its grid cell.
        /// Adjacent triangles that share a 3D edge but reference different
        /// mesh.vertices entries (UV/normal seam duplicates) collapse onto
        /// the same canonical edge. Output length == tris.Length.</summary>
        static int[] BuildCanonicalIndices(Vector3[] worldVerts, int[] tris, float meshDiag)
        {
            int vn = worldVerts.Length;
            // Per-vertex canonical ID; -1 until assigned.
            var canonical = new int[vn];
            for (int i = 0; i < vn; i++) canonical[i] = -1;

            float cell = Mathf.Max(meshDiag, 1f) * 1e-5f;
            float invCell = 1f / cell;
            // Use a ValueTuple key so coords aren't bit-packed (21-bit packing
            // wraps for meshes far from world origin — a 1m-diag mesh at world
            // position 50m generates kx ≈ 5e6, well past 2²¹ ≈ 2M).
            var grid = new Dictionary<(long, long, long), int>(vn);
            int next = 0;
            // Only canonicalize vertices actually used by triangles — unused
            // mesh.vertices entries (common in stripped meshes) would
            // otherwise pollute the grid.
            for (int t = 0; t < tris.Length; t++)
            {
                int vi = tris[t];
                if (canonical[vi] >= 0) continue;
                var p = worldVerts[vi];
                var key = ((long)Mathf.Floor(p.x * invCell),
                           (long)Mathf.Floor(p.y * invCell),
                           (long)Mathf.Floor(p.z * invCell));
                if (!grid.TryGetValue(key, out int id))
                {
                    id = next++;
                    grid[key] = id;
                }
                canonical[vi] = id;
            }
            // Rewrite tris through the canonical map so the caller can index
            // it directly as canonicalTris[f*3 + k].
            var rewritten = new int[tris.Length];
            for (int t = 0; t < tris.Length; t++)
                rewritten[t] = canonical[tris[t]];
            return rewritten;
        }

        static float ComputeMeshDiagonal(Mesh mesh, Transform xform)
        {
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0) return 0f;
            var p0 = xform.TransformPoint(verts[0]);
            Vector3 lo = p0, hi = p0;
            for (int i = 1; i < verts.Length; i++)
            {
                var p = xform.TransformPoint(verts[i]);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            return (hi - lo).magnitude;
        }

        // ─── Shell extraction (union-find on face adjacency) ─────────

        static Shell3D[] ExtractShells(Face3D[] faces, Vector3[] worldVerts, int[] rawTris,
            int[] canonicalTris, float thresholdDeg, float shellMergeAngleDeg)
        {
            var faceToShell = new int[faces.Length];
            return ExtractShells(faces, worldVerts, rawTris, canonicalTris, thresholdDeg,
                shellMergeAngleDeg, faceToShell, null);
        }

        /// <summary>Variant that also fills <paramref name="faceToShellOut"/> with the
        /// shell index per face (or -1 for degenerate faces / faces excluded by
        /// <paramref name="participateMask"/>). The array must be pre-allocated
        /// to faces.Length. <paramref name="canonicalTris"/> must contain
        /// position-deduplicated vertex indices (see <see cref="BuildCanonicalIndices"/>).
        /// <paramref name="participateMask"/> (optional, may be null): only faces
        /// with mask[f] == true participate in shell formation; the rest get
        /// faceToShellOut[f] = -1. Used to cluster the subset of fine-LOD faces
        /// flagged for promotion by the projective classifier.
        /// <paramref name="shellMergeAngleDeg"/> (≤0 disables) controls the
        /// second-pass merge of adjacent shells whose dominant normals agree
        /// within this many degrees — see <see cref="MergeAdjacentShells"/>.</summary>
        static Shell3D[] ExtractShells(Face3D[] faces, Vector3[] worldVerts, int[] rawTris,
            int[] canonicalTris, float thresholdDeg, float shellMergeAngleDeg,
            int[] faceToShellOut, bool[] participateMask)
        {
            int n = faces.Length;
            if (n == 0) return new Shell3D[0];
            // Adjacency: edge → face list. Edge key packs (min(va,vb), max).
            // Degenerate faces (area==0) contribute no edges — they remain
            // singleton "roots" but get filtered to faceToShellOut = -1 below.
            var edgeFaces = new Dictionary<long, List<int>>(n * 3);
            void AddEdge(int va, int vb, int face)
            {
                long key = va < vb
                    ? ((long)va << 32) | (uint)vb
                    : ((long)vb << 32) | (uint)va;
                if (!edgeFaces.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    edgeFaces[key] = list;
                }
                list.Add(face);
            }
            for (int f = 0; f < n; f++)
            {
                if (faces[f].area <= 0f) continue;
                if (participateMask != null && !participateMask[f]) continue;
                int v0 = canonicalTris[f * 3], v1 = canonicalTris[f * 3 + 1], v2 = canonicalTris[f * 3 + 2];
                AddEdge(v0, v1, f); AddEdge(v1, v2, f); AddEdge(v2, v0, f);
            }

            float thresholdCos = Mathf.Cos(thresholdDeg * Mathf.Deg2Rad);
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            foreach (var kv in edgeFaces)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        float d = Vector3.Dot(faces[list[i]].normal, faces[list[j]].normal);
                        if (d >= thresholdCos) Union(list[i], list[j]);
                    }
            }

            // Materialise shells with area-weighted centroid + normal, plus
            // a local 2D basis derived from the dominant normal (used by
            // InverseTransfer to project 3D verts into the domain's atlas rect).
            var rootToShell = new Dictionary<int, int>();
            var faces_ = new List<List<int>>();
            var accumNormal = new List<Vector3>();
            var accumCentroid = new List<Vector3>();
            var accumArea = new List<float>();

            for (int f = 0; f < n; f++)
            {
                if (faces[f].area <= 0f ||
                    (participateMask != null && !participateMask[f]))
                {
                    faceToShellOut[f] = -1;
                    continue;
                }
                int r = Find(f);
                if (!rootToShell.TryGetValue(r, out int si))
                {
                    si = faces_.Count;
                    rootToShell[r] = si;
                    faces_.Add(new List<int>());
                    accumNormal.Add(Vector3.zero);
                    accumCentroid.Add(Vector3.zero);
                    accumArea.Add(0f);
                }
                faceToShellOut[f] = si;
                faces_[si].Add(f);
                float a = faces[f].area;
                accumNormal[si]   = accumNormal[si]   + faces[f].normal   * a;
                accumCentroid[si] = accumCentroid[si] + faces[f].centroid * a;
                accumArea[si]     = accumArea[si]     + a;
            }

            var shells = new Shell3D[faces_.Count];
            for (int si = 0; si < shells.Length; si++)
            {
                shells[si].faceIndices = faces_[si];
                shells[si].faceCount   = faces_[si].Count;
                float ta = accumArea[si];
                shells[si].totalArea   = ta;
                if (ta > 1e-12f)
                {
                    shells[si].centroid = accumCentroid[si] / ta;
                    var nn = accumNormal[si] / ta;
                    float m = nn.magnitude;
                    shells[si].dominantNormal = m > 1e-12f ? nn / m : Vector3.up;
                }
                else
                {
                    shells[si].dominantNormal = Vector3.up;
                    shells[si].centroid       = Vector3.zero;
                }
                ComputePlaneBasis(shells[si].dominantNormal, out shells[si].basisU, out shells[si].basisV);
                ComputeExtents(worldVerts, rawTris, faces_[si], shells[si].centroid,
                    shells[si].basisU, shells[si].basisV,
                    out shells[si].extentU, out shells[si].extentV);
            }

            // Optional Step 2 merge: collapse adjacent shells whose dominant
            // normals are within shellMergeAngleDeg. The caller passes a
            // negative or zero threshold (or omits the overload) to disable.
            if (shellMergeAngleDeg > 0f && shells.Length > 1)
                shells = MergeAdjacentShells(shells, faces, worldVerts, rawTris,
                    canonicalTris, faceToShellOut, shellMergeAngleDeg);

            return shells;
        }

        /// <summary>Second-pass shell merge: union-find over shell indices, with
        /// adjacency = "shells share at least one canonical edge" and the
        /// merge predicate = "shells' dominant normals are within
        /// <paramref name="mergeAngleDeg"/>". Compensates for the per-face
        /// extraction being too strict at shell boundaries — a single noisy
        /// triangle on a planked wall can otherwise split it into two base
        /// shells, breaking face-consensus for any fine-LOD face that
        /// straddles the split. Adjacency requirement is critical: it prevents
        /// merging two physically separate shells that happen to face the
        /// same direction (floor and table top both normal=+Y, but never
        /// share an edge).</summary>
        static Shell3D[] MergeAdjacentShells(Shell3D[] shells, Face3D[] faces,
            Vector3[] worldVerts, int[] rawTris, int[] canonicalTris,
            int[] faceToShellOut, float mergeAngleDeg)
        {
            // Build shell-adjacency set via canonical edges. An edge belongs
            // to a shell if any of its incident faces does; two shells are
            // adjacent if they both claim the same canonical edge.
            int n = faces.Length;
            var edgeShell = new Dictionary<long, int>(n * 3);
            var adjPairs = new HashSet<long>();
            for (int f = 0; f < n; f++)
            {
                int s = faceToShellOut[f];
                if (s < 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    int va = canonicalTris[f * 3 + k];
                    int vb = canonicalTris[f * 3 + (k + 1) % 3];
                    long ekey = va < vb
                        ? ((long)va << 32) | (uint)vb
                        : ((long)vb << 32) | (uint)va;
                    if (edgeShell.TryGetValue(ekey, out int other))
                    {
                        if (other != s)
                        {
                            long pair = s < other
                                ? ((long)s     << 32) | (uint)other
                                : ((long)other << 32) | (uint)s;
                            adjPairs.Add(pair);
                        }
                    }
                    else edgeShell[ekey] = s;
                }
            }
            if (adjPairs.Count == 0) return shells;

            // Union-find over shells with angular threshold.
            var parent = new int[shells.Length];
            for (int i = 0; i < shells.Length; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            float cosThr = Mathf.Cos(mergeAngleDeg * Mathf.Deg2Rad);
            foreach (long pair in adjPairs)
            {
                int s1 = (int)(pair >> 32);
                int s2 = (int)(pair & 0xFFFFFFFFL);
                float dot = Vector3.Dot(shells[s1].dominantNormal,
                                         shells[s2].dominantNormal);
                if (dot >= cosThr)
                {
                    int r1 = Find(s1), r2 = Find(s2);
                    if (r1 != r2) parent[r1] = r2;
                }
            }

            // Compact: build new shell list, one entry per unique root.
            var rootToNew = new Dictionary<int, int>();
            var newFaceLists = new List<List<int>>();
            int[] oldToNew = new int[shells.Length];
            for (int i = 0; i < shells.Length; i++)
            {
                int r = Find(i);
                if (!rootToNew.TryGetValue(r, out int ni))
                {
                    ni = newFaceLists.Count;
                    rootToNew[r] = ni;
                    newFaceLists.Add(new List<int>());
                }
                oldToNew[i] = ni;
            }
            if (newFaceLists.Count == shells.Length) return shells; // no merges

            // Re-thread faces into new shells + update faceToShellOut.
            for (int f = 0; f < faceToShellOut.Length; f++)
            {
                int s = faceToShellOut[f];
                if (s < 0) continue;
                int ns = oldToNew[s];
                faceToShellOut[f] = ns;
                newFaceLists[ns].Add(f);
            }

            // Re-aggregate centroid / normal / extent for each merged shell.
            var merged = new Shell3D[newFaceLists.Count];
            for (int ni = 0; ni < merged.Length; ni++)
            {
                var faceIdx = newFaceLists[ni];
                Vector3 nAccum = Vector3.zero, cAccum = Vector3.zero;
                float aAccum = 0f;
                foreach (int f in faceIdx)
                {
                    float a = faces[f].area;
                    nAccum += faces[f].normal   * a;
                    cAccum += faces[f].centroid * a;
                    aAccum += a;
                }
                merged[ni].faceIndices = faceIdx;
                merged[ni].faceCount   = faceIdx.Count;
                merged[ni].totalArea   = aAccum;
                if (aAccum > 1e-12f)
                {
                    merged[ni].centroid = cAccum / aAccum;
                    var nn = nAccum / aAccum;
                    float m = nn.magnitude;
                    merged[ni].dominantNormal = m > 1e-12f ? nn / m : Vector3.up;
                }
                else
                {
                    merged[ni].dominantNormal = Vector3.up;
                    merged[ni].centroid       = Vector3.zero;
                }
                ComputePlaneBasis(merged[ni].dominantNormal,
                    out merged[ni].basisU, out merged[ni].basisV);
                ComputeExtents(worldVerts, rawTris, faceIdx, merged[ni].centroid,
                    merged[ni].basisU, merged[ni].basisV,
                    out merged[ni].extentU, out merged[ni].extentV);
            }
            return merged;
        }

        // ─── Per-vertex projection (PR-2.7 — Frostbite-style) ────────

        /// <summary>Closest point on triangle ABC to query point P. Standard
        /// Voronoi-region algorithm (Ericson, Real-Time Collision Detection
        /// ch. 5). No allocations, ~30 ops, branch-heavy.</summary>
        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return a + v * ab;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + w * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float vv = vb * denom;
            float ww = vc * denom;
            return a + ab * vv + ac * ww;
        }

        /// <summary>Squared distance from point q to AABB [mn, mx]; 0 if inside.
        /// Used as an early-out filter before the expensive triangle test.</summary>
        static float SqDistToAabb(Vector3 q, Vector3 mn, Vector3 mx)
        {
            float dx = q.x < mn.x ? mn.x - q.x : (q.x > mx.x ? q.x - mx.x : 0f);
            float dy = q.y < mn.y ? mn.y - q.y : (q.y > mx.y ? q.y - mx.y : 0f);
            float dz = q.z < mn.z ? mn.z - q.z : (q.z > mx.z ? q.z - mx.z : 0f);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>Precomputed per-triangle AABBs for the deepest-LOD mesh —
        /// pays for itself after ~3 vertex queries vs computing on the fly.</summary>
        static void BuildDeepAabbs(Vector3[] worldVerts, int[] rawTris,
            out Vector3[] mins, out Vector3[] maxs)
        {
            int n = rawTris.Length / 3;
            mins = new Vector3[n];
            maxs = new Vector3[n];
            for (int f = 0; f < n; f++)
            {
                var a = worldVerts[rawTris[f * 3]];
                var b = worldVerts[rawTris[f * 3 + 1]];
                var c = worldVerts[rawTris[f * 3 + 2]];
                mins[f] = Vector3.Min(Vector3.Min(a, b), c);
                maxs[f] = Vector3.Max(Vector3.Max(a, b), c);
            }
        }

        /// <summary>Find the deepest-LOD triangle whose surface is closest to
        /// world-space query point <paramref name="q"/>. Brute-force scan with
        /// AABB rejection — O(N) tris per query, but on our worst test models
        /// (~4k deep tris × ~12k fine verts) totals well under a second. A BVH
        /// is a follow-up if profiling justifies it. Returns -1 if the deep
        /// mesh is empty.</summary>
        static int ProjectVertexToDeepMesh(Vector3 q,
            Face3D[] deepFaces, Vector3[] deepWorldVerts, int[] deepRawTris,
            Vector3[] aabbMin, Vector3[] aabbMax, out float bestDist)
        {
            int closest = -1;
            float bestSq = float.MaxValue;
            int n = deepFaces.Length;
            for (int f = 0; f < n; f++)
            {
                if (deepFaces[f].area <= 0f) continue;
                if (SqDistToAabb(q, aabbMin[f], aabbMax[f]) >= bestSq) continue;
                var a = deepWorldVerts[deepRawTris[f * 3]];
                var b = deepWorldVerts[deepRawTris[f * 3 + 1]];
                var c = deepWorldVerts[deepRawTris[f * 3 + 2]];
                Vector3 pt = ClosestPointOnTriangle(q, a, b, c);
                float dsq = (pt - q).sqrMagnitude;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    closest = f;
                }
            }
            bestDist = closest >= 0 ? Mathf.Sqrt(bestSq) : float.PositiveInfinity;
            return closest;
        }

        /// <summary>Materialise a fine-LOD shell (collected from the promote-
        /// pile after projective classification) as a <see cref="PromotedCluster"/>.
        /// The shell already has area-weighted plane data + vertex-based
        /// extents — just copy fields and tag the source LOD.</summary>
        static PromotedCluster MakePromotedClusterFromShell(Shell3D shell, int sourceLodIndex)
        {
            return new PromotedCluster
            {
                faceIndices    = shell.faceIndices,
                centroid       = shell.centroid,
                dominantNormal = shell.dominantNormal,
                basisU         = shell.basisU,
                basisV         = shell.basisV,
                extentU        = shell.extentU,
                extentV        = shell.extentV,
                totalArea      = shell.totalArea,
                sourceLodIndex = sourceLodIndex,
            };
        }

        // ─── Plane basis + extents ───────────────────────────────────

        /// <summary>Pick two orthonormal vectors in the plane normal to <paramref name="n"/>.
        /// Algorithm: take any axis not parallel to n, cross to get basisU, cross again for basisV.</summary>
        static void ComputePlaneBasis(Vector3 n, out Vector3 u, out Vector3 v)
        {
            Vector3 helper = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            u = Vector3.Cross(n, helper).normalized;
            v = Vector3.Cross(n, u).normalized;
        }

        /// <summary>Project every CORNER VERTEX of every face in the shell onto
        /// (u,v) basis centred at <paramref name="origin"/>; max abs value along
        /// each axis becomes the half-extent. Vertex-based (not centroid-based)
        /// because InverseTransfer will project the SAME vertices into the
        /// atlas rect; a centroid-based extent under-shoots long thin triangles
        /// (corner verts spill outside the rect → wrap-around bleeding in the
        /// baked lightmap).</summary>
        static void ComputeExtents(Vector3[] worldVerts, int[] rawTris,
            List<int> faceIndices, Vector3 origin,
            Vector3 u, Vector3 v, out float extU, out float extV)
        {
            float maxU = 0f, maxV = 0f;
            foreach (int f in faceIndices)
            {
                for (int k = 0; k < 3; k++)
                {
                    Vector3 d = worldVerts[rawTris[f * 3 + k]] - origin;
                    float pu = Mathf.Abs(Vector3.Dot(d, u));
                    float pv = Mathf.Abs(Vector3.Dot(d, v));
                    if (pu > maxU) maxU = pu;
                    if (pv > maxV) maxV = pv;
                }
            }
            // Small floor — degenerate shells (1-2 tiny triangles) would otherwise
            // have zero extent and divide-by-zero in InverseTransfer's mapping.
            const float kMinExtent = 1e-4f;
            extU = Mathf.Max(maxU, kMinExtent);
            extV = Mathf.Max(maxV, kMinExtent);
        }

        // ─── Domain materialisation ──────────────────────────────────

        static LightingDomain MakeDomainFromShell(Shell3D shell, bool isPromoted, int sourceLodIndex)
            => new LightingDomain
            {
                planeCentroid  = shell.centroid,
                planeNormal    = shell.dominantNormal,
                planeU         = shell.basisU,
                planeV         = shell.basisV,
                planeExtentU   = shell.extentU,
                planeExtentV   = shell.extentV,
                isPromoted     = isPromoted,
                sourceLodIndex = sourceLodIndex,
                faceCount      = shell.faceCount,
                totalArea3D    = shell.totalArea,
                uv2Rect        = new Rect(0, 0, 0, 0), // filled by packer
            };

        static LightingDomain MakeDomainFromCluster(PromotedCluster c)
            => new LightingDomain
            {
                planeCentroid  = c.centroid,
                planeNormal    = c.dominantNormal,
                planeU         = c.basisU,
                planeV         = c.basisV,
                planeExtentU   = c.extentU,
                planeExtentV   = c.extentV,
                isPromoted     = true,
                sourceLodIndex = c.sourceLodIndex,
                faceCount      = c.faceIndices.Count,
                totalArea3D    = c.totalArea,
                uv2Rect        = new Rect(0, 0, 0, 0),
            };

        // ─── Naive horizontal-strip atlas packer ─────────────────────

        /// <summary>
        /// PR-2 placeholder packer. Sorts domains by descending plane area, then
        /// packs them left-to-right into rows of <paramref name="opts"/>.atlasResolutionPx
        /// pixels wide. Atlas grows downward as rows fill up. Each domain gets a
        /// pixel rect proportional to its planar (extentU × 2, extentV × 2) at a
        /// uniform texels-per-world-unit derived from the max domain. The rect is
        /// then normalized to [0,1]² and stored in <see cref="LightingDomain.uv2Rect"/>.
        ///
        /// PR-2.8 swaps this for xatlas via XatlasNative. The contract is the same:
        /// after the call, every domain has a valid uv2Rect and atlasW/H are the
        /// final dimensions.
        /// </summary>
        static void PackAtlasNaive(LightingDomain[] domains, Options opts,
            out int atlasW, out int atlasH)
        {
            atlasW = Mathf.Max(64, opts.atlasResolutionPx);
            atlasH = 1;
            if (domains == null || domains.Length == 0) return;

            // Find the largest plane diagonal across all domains so we can pick
            // texels-per-world-unit such that the biggest domain occupies about
            // half the atlas width (leaves room for siblings on the same row).
            float maxDiag = 0f;
            for (int i = 0; i < domains.Length; i++)
            {
                float diag = 2f * Mathf.Sqrt(domains[i].planeExtentU * domains[i].planeExtentU
                                           + domains[i].planeExtentV * domains[i].planeExtentV);
                if (diag > maxDiag) maxDiag = diag;
            }
            if (maxDiag < 1e-6f) maxDiag = 1f;
            float texelsPerWorld = (atlasW * 0.5f) / maxDiag;

            // Sort by descending pixel area (biggest first) for better strip packing.
            var order = new int[domains.Length];
            for (int i = 0; i < domains.Length; i++) order[i] = i;
            var widths  = new int[domains.Length];
            var heights = new int[domains.Length];
            for (int i = 0; i < domains.Length; i++)
            {
                int w = Mathf.Max(2, Mathf.CeilToInt(2f * domains[i].planeExtentU * texelsPerWorld));
                int h = Mathf.Max(2, Mathf.CeilToInt(2f * domains[i].planeExtentV * texelsPerWorld));
                widths[i] = w; heights[i] = h;
            }
            Array.Sort(order, (a, b) => (widths[b] * heights[b]).CompareTo(widths[a] * heights[a]));

            int pad = Mathf.Max(0, opts.interDomainPaddingPx);
            int cursorX = pad, cursorY = pad, rowH = 0;
            var pxRects = new RectInt[domains.Length];
            for (int oi = 0; oi < order.Length; oi++)
            {
                int i = order[oi];
                int w = widths[i], h = heights[i];
                if (cursorX + w + pad > atlasW)
                {
                    cursorX = pad;
                    cursorY += rowH + pad;
                    rowH = 0;
                }
                pxRects[i] = new RectInt(cursorX, cursorY, w, h);
                cursorX += w + pad;
                if (h > rowH) rowH = h;
            }
            atlasH = Mathf.Max(64, cursorY + rowH + pad);

            // Normalize to [0,1] using the final atlas dimensions.
            float invW = 1f / atlasW;
            float invH = 1f / atlasH;
            for (int i = 0; i < domains.Length; i++)
            {
                var r = pxRects[i];
                domains[i].uv2Rect = new Rect(r.x * invW, r.y * invH, r.width * invW, r.height * invH);
            }
        }

        // ─── Public callable for the unified benchmark orchestrator ──

        /// <summary>Build the hierarchical atlas for a single LODGroup and write
        /// the dry-run CSV into <paramref name="outputDir"/> as
        /// <c>repack.csv</c> plus diagnostic PNGs. Returns the build
        /// <see cref="Result"/>; the
        /// caller can inspect counters or surface a per-case summary. This is
        /// the entry point used by <c>LightmapTransferTool.ExecBenchmark</c>;
        /// stand-alone single-model dry-runs are no longer wired to a
        /// dedicated menu — the unified benchmark covers that workflow.</summary>
        public static Result BuildAndWriteForCase(LODGroup lg, Options opts, string outputDir)
        {
            var result = Build(lg, opts);
            if (!string.IsNullOrEmpty(result.error)) return result;
            WriteDryRunReport(lg.name, result, outputDir);
            WriteAtlasPng(outputDir, result);
            WriteProxyUv2Png(outputDir, result);
            WriteProxySamplesPng(outputDir, result);
            WriteProxyHitsPngs(outputDir, lg, result);
            WriteFinalUv2Pngs(outputDir, lg, result);
            WriteFineLodDomainPngs(outputDir, lg, result);
            LogDryRunSummary(lg.name, result);
            return result;
        }

        /// <summary>PR-3 Stage 1 visualization: render up to three proxy UV2
        /// candidates as flat UV layout PNGs for side-by-side comparison.
        ///   proxy_uv2_clean.png — UV0 → sym-split → xatlas pack (ARAP on)
        ///   proxy_uv2_raw.png   — UV0 → xatlas pack only (no sym-split,
        ///                         no ARAP). Shows what those steps add.
        ///   proxy_uv2_auto.png  — TRUE auto-unwrap from positions + normals
        ///                         (no UV0 input). Needs the native bridge's
        ///                         xatlasAddMesh export (post DLL rebuild).
        /// Each PNG is skipped if its variant failed to produce data.</summary>
        static void WriteProxyUv2Png(string outputDir, Result r)
        {
            if (r.proxyUv2Clean != null && r.proxyTrisClean != null)
                UvPngWriter.Render(Path.Combine(outputDir, "proxy_uv2_clean.png"),
                    r.proxyUv2Clean, r.proxyTrisClean);
            if (r.proxyUv2Raw != null && r.proxyTrisRaw != null)
                UvPngWriter.Render(Path.Combine(outputDir, "proxy_uv2_raw.png"),
                    r.proxyUv2Raw, r.proxyTrisRaw);
            if (r.proxyUv2Auto != null && r.proxyTrisAuto != null)
                UvPngWriter.Render(Path.Combine(outputDir, "proxy_uv2_auto.png"),
                    r.proxyUv2Auto, r.proxyTrisAuto);
        }

        /// <summary>Generate the three proxy UV2 candidates documented at
        /// <see cref="WriteProxyUv2Png"/>. Each populates a pair of
        /// (proxyUv2*, proxyTris*) fields on the Result. Variants that
        /// fail individually log a warning but don't abort the others.</summary>
        static void ComputeProxyUv2Variants(Mesh deepMesh, Transform deepXform,
            Options opts, string lgName, Result result)
        {
            if (deepMesh == null) return;

            // ── Variant 1: clean (sym-split + ARAP + pack) ──
            if (deepMesh.uv != null && deepMesh.uv.Length > 0)
            {
                var clone = UnityEngine.Object.Instantiate(deepMesh);
                clone.name = deepMesh.name + "_proxy_clean";
                try
                {
                    var shells = UvShellExtractor.Extract(clone.uv, clone.triangles);
                    if (shells != null && shells.Count > 0)
                    {
                        int split = SymmetrySplitShells.Split(clone, shells);
                        if (split > 0)
                            UvtLog.Info(UvtLog.Category.Benchmark,
                                $"[HierRepack] proxy sym-split on '{lgName}': {split} shells split");
                    }
                    // Call RepackSingle directly (bypass RepackUv) so we can
                    // disable xatlas's 90° chart rotation. The diagnostic
                    // operator reads chart orientation as a feature of the
                    // proxy layout — auto-rotating for ~5% extra packing
                    // density rotates diagonal triangulations relative to
                    // the other variants and reads as "разворот".
                    var cleanOpts = RepackOptions.Default;
                    cleanOpts.resolution   = (uint)opts.atlasResolutionPx;
                    cleanOpts.padding      = (uint)opts.interDomainPaddingPx;
                    cleanOpts.rotateCharts = false;
                    var packed = XatlasRepack.RepackSingle(clone, cleanOpts).ok
                        ? clone.uv2 : null;
                    if (packed != null && packed.Length > 0)
                    {
                        result.proxyUv2Clean   = packed;
                        result.proxyTrisClean  = clone.triangles;
                        result.proxyWorldVertsClean = ToWorld(clone.vertices, deepXform);
                    }
                }
                catch (Exception ex)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] proxy clean variant failed on '{lgName}': {ex.Message}");
                }
                finally { UnityEngine.Object.DestroyImmediate(clone); }
            }

            // ── Variant 2: raw (UV0 → pack only) ──
            if (deepMesh.uv != null && deepMesh.uv.Length > 0)
            {
                try
                {
                    var rawOpts = RepackOptions.Default;
                    rawOpts.resolution                   = (uint)opts.atlasResolutionPx;
                    rawOpts.padding                      = (uint)opts.interDomainPaddingPx;
                    rawOpts.reparameterizeStretchedShells = false; // disable ARAP
                    rawOpts.rotateCharts                 = false;  // preserve orientation
                    var clone = UnityEngine.Object.Instantiate(deepMesh);
                    clone.name = deepMesh.name + "_proxy_raw";
                    try
                    {
                        var res = XatlasRepack.RepackSingle(clone, rawOpts);
                        if (res.ok)
                        {
                            var uvOut = new List<Vector2>();
                            clone.GetUVs(1, uvOut);
                            result.proxyUv2Raw       = uvOut.ToArray();
                            result.proxyTrisRaw      = clone.triangles;
                            result.proxyWorldVertsRaw = ToWorld(clone.vertices, deepXform);
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(clone); }
                }
                catch (Exception ex)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] proxy raw variant failed on '{lgName}': {ex.Message}");
                }
            }

            // ── Variant 3: true auto-unwrap (positions + normals) ──
            try
            {
                AutoUnwrapDeepMesh(deepMesh, deepXform, opts,
                    out var uvAuto, out var trisAuto, out var worldAuto);
                if (uvAuto != null && trisAuto != null && worldAuto != null)
                {
                    result.proxyUv2Auto        = uvAuto;
                    result.proxyTrisAuto       = trisAuto;
                    result.proxyWorldVertsAuto = worldAuto;
                }
            }
            catch (Exception ex)
            {
                // xatlasAddMesh missing from the DLL = DllNotFoundException /
                // EntryPointNotFoundException; treat as "feature not yet
                // available", don't spam an Error.
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] proxy auto-unwrap unavailable on '{lgName}': {ex.GetType().Name} ({ex.Message})");
            }

            // ── Select the active proxy that Stage 2+ will consume ──
            // All three variants are still emitted as diagnostic PNGs;
            // this just decides which pair the downstream sampler reads.
            // Fall back to Clean if the selected variant didn't produce
            // data (e.g. Auto failed before the DLL rebuild propagated).
            switch (opts.proxyMode)
            {
                case ProxyMode.Raw:
                    if (result.proxyUv2Raw != null)
                    {
                        result.proxyUv2         = result.proxyUv2Raw;
                        result.proxyTris        = result.proxyTrisRaw;
                        result.proxyWorldVerts  = result.proxyWorldVertsRaw;
                    }
                    break;
                case ProxyMode.Auto:
                    if (result.proxyUv2Auto != null)
                    {
                        result.proxyUv2         = result.proxyUv2Auto;
                        result.proxyTris        = result.proxyTrisAuto;
                        result.proxyWorldVerts  = result.proxyWorldVertsAuto;
                    }
                    break;
            }
            // Default + fallback: Clean.
            if (result.proxyUv2 == null && result.proxyUv2Clean != null)
            {
                result.proxyUv2         = result.proxyUv2Clean;
                result.proxyTris        = result.proxyTrisClean;
                result.proxyWorldVerts  = result.proxyWorldVertsClean;
            }
        }

        /// <summary>Transform a local-space vertex array into world space
        /// using <paramref name="xform"/>. Helper for proxy variants that
        /// need to expose their post-mod vertex positions to Stage 2+ —
        /// sym-split (clean) and chart-seam splitting (auto) both rewrite
        /// the proxy vertex layout, so we can't reuse the original deep
        /// mesh's world verts.</summary>
        static Vector3[] ToWorld(Vector3[] local, Transform xform)
        {
            if (local == null) return null;
            var w = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++) w[i] = xform.TransformPoint(local[i]);
            return w;
        }

        // ─── PR-3 Stage 2: Poisson sampling on proxy surface ─────────

        /// <summary>Generate uniform-density samples across the active
        /// proxy's 3D surface using stratified jittered barycentrics per
        /// triangle (a close-enough Poisson approximation for the
        /// diagnostic; true blue-noise can replace this later without
        /// changing call sites). Each sample carries the world position
        /// and the proxy UV2 it inherits — Stage 3 will project these
        /// onto each fine LOD's surface and use the {3D, UV2} pairs to
        /// fit a per-fine-shell affine transform.</summary>
        static void GenerateProxySamples(Options opts, float meshDiag, Result r)
        {
            if (r.proxyUv2 == null || r.proxyTris == null || r.proxyWorldVerts == null)
                return;
            if (r.proxyTris.Length < 3) return;

            // Per-face proxy-shell membership via UV2-edge connectivity.
            // UvShellExtractor was written for UV0 input, but the partition
            // rule (two faces share a shell iff their shared canonical edge
            // has matching UV on both sides) is the same on UV2.
            var uvShells = UvShellExtractor.Extract(r.proxyUv2, r.proxyTris);
            int faceCount = r.proxyTris.Length / 3;
            var faceToShell = new int[faceCount];
            for (int i = 0; i < faceCount; i++) faceToShell[i] = -1;
            if (uvShells != null)
            {
                for (int si = 0; si < uvShells.Count; si++)
                    foreach (int f in uvShells[si].faceIndices)
                        if (f >= 0 && f < faceCount)
                            faceToShell[f] = si;
            }

            float diagSq = Mathf.Max(meshDiag * meshDiag, 1e-6f);
            float densityPerSqMeshDiag = Mathf.Max(opts.proxySampleDensity, 1);
            var samples = new List<ProxySample>(faceCount * 6);
            int nextSampleId = 0;
            // Use a deterministic LCG so re-runs on the same mesh produce
            // identical sample sets — keeps the visual diagnostic stable.
            uint rng = 0x9E3779B9u;

            // Pre-pass: compute the median tri UV bbox. Bridge tris (one
            // vertex at the "winning" chart, two at the "losing" chart)
            // have a UV bbox an order of magnitude larger than chart-
            // interior tris, so any tri > 8× median is almost certainly a
            // rogue. The absolute 0.35 cap below catches catastrophic
            // bridges even when the chart median is itself bloated; the
            // adaptive multiplier cleans up the long tail of small-to-
            // medium bridges that the constant filter missed on the
            // Carousel / Wooden_Box_Long.
            var triUvBboxMax = new float[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                int ia = r.proxyTris[f * 3];
                int ib = r.proxyTris[f * 3 + 1];
                int ic = r.proxyTris[f * 3 + 2];
                Vector2 uvA = r.proxyUv2[ia];
                Vector2 uvB = r.proxyUv2[ib];
                Vector2 uvC = r.proxyUv2[ic];
                float minX = Mathf.Min(uvA.x, Mathf.Min(uvB.x, uvC.x));
                float maxX = Mathf.Max(uvA.x, Mathf.Max(uvB.x, uvC.x));
                float minY = Mathf.Min(uvA.y, Mathf.Min(uvB.y, uvC.y));
                float maxY = Mathf.Max(uvA.y, Mathf.Max(uvB.y, uvC.y));
                triUvBboxMax[f] = Mathf.Max(maxX - minX, maxY - minY);
            }
            float adaptiveBbox;
            {
                var sorted = (float[])triUvBboxMax.Clone();
                Array.Sort(sorted);
                float median = sorted[sorted.Length / 2];
                // Floor at 0.01 so a chart that happens to have all tiny
                // tris (heavily subdivided panel) doesn't make the
                // adaptive threshold absurdly small.
                adaptiveBbox = Mathf.Max(0.01f, median * 8f);
            }

            for (int f = 0; f < faceCount; f++)
            {
                int ia = r.proxyTris[f * 3];
                int ib = r.proxyTris[f * 3 + 1];
                int ic = r.proxyTris[f * 3 + 2];
                Vector3 A = r.proxyWorldVerts[ia];
                Vector3 B = r.proxyWorldVerts[ib];
                Vector3 C = r.proxyWorldVerts[ic];
                Vector2 uvA = r.proxyUv2[ia];
                Vector2 uvB = r.proxyUv2[ib];
                Vector2 uvC = r.proxyUv2[ic];

                // Skip rogue tris produced by xatlas chart-seam splits
                // that AssignUv2 had to collapse back to a single UV per
                // shared vertex: the losing chart's tris end up with one
                // corner at the WINNER's UV (in a totally different
                // atlas region), forming a long thin tri that bridges
                // two unrelated charts. Two layered filters:
                //   1. Absolute cap (0.35) catches catastrophic bridges
                //      that span large fractions of the unit box.
                //   2. Adaptive cap (8× chart median) catches the long
                //      tail of medium-spread bridges that the absolute
                //      filter let through (responsible for the scatter
                //      dots in the gray inter-chart areas of Carousel /
                //      Wooden_Box_Long proxy_samples.png).
                const float kUvSeamBboxAbs = 0.35f;
                if (triUvBboxMax[f] > kUvSeamBboxAbs) continue;
                if (triUvBboxMax[f] > adaptiveBbox)   continue;

                Vector3 cross = Vector3.Cross(B - A, C - A);
                float crossMag = cross.magnitude;
                float triArea = 0.5f * crossMag;
                if (triArea <= 0f) continue;
                Vector3 faceNormal = cross / crossMag;
                int shellId = faceToShell[f];

                int n = Mathf.Max(3, Mathf.CeilToInt(densityPerSqMeshDiag * triArea / diagSq));

                for (int s = 0; s < n; s++)
                {
                    rng = unchecked(rng * 1664525u + 1013904223u);
                    float u = (rng & 0xFFFFFFu) / 16777216f;
                    rng = unchecked(rng * 1664525u + 1013904223u);
                    float v = (rng & 0xFFFFFFu) / 16777216f;
                    // Uniform sampling on a triangle via the
                    // sqrt-of-uniform reflection.
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    float w = 1f - u - v;
                    samples.Add(new ProxySample
                    {
                        worldPos     = A * w + B * u + C * v,
                        worldNormal  = faceNormal,
                        uv2          = uvA * w + uvB * u + uvC * v,
                        proxyFaceIdx = f,
                        proxyShellId = shellId,
                        sampleId     = nextSampleId++,
                    });
                }
            }
            r.proxySamples = samples.ToArray();
        }

        /// <summary>PR-3 Stage 2 visualization: overlay the proxy samples
        /// as small dots on the active proxy's UV2 layout. The base
        /// triangulation is drawn faintly so the sample distribution is
        /// the dominant visual signal.</summary>
        static void WriteProxySamplesPng(string outputDir, Result r)
        {
            if (r.proxySamples == null || r.proxySamples.Length == 0) return;
            if (r.proxyUv2 == null || r.proxyTris == null) return;

            // Base: render the active proxy's UV layout as a faded backdrop.
            string basePath = Path.Combine(outputDir, "proxy_uv2_active.png");
            UvPngWriter.Render(basePath, r.proxyUv2, r.proxyTris);

            // Now draw samples on top of a fresh canvas with the same
            // backdrop. Software path: load the rendered base, draw dots.
            int size = UvPngWriter.DefaultSize;
            var pixels = new Color32[size * size];
            byte[] basePng = File.ReadAllBytes(basePath);
            var baseTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                baseTex.LoadImage(basePng);
                var basePixels = baseTex.GetPixels32();
                // Render may produce different dimensions; just trust loaded size.
                int w = baseTex.width, h = baseTex.height;
                if (w * h == basePixels.Length && w == size && h == size)
                    pixels = basePixels;
                else
                {
                    var bg = new Color32(244, 244, 248, 255);
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(baseTex); }

            // Draw each sample as a 3-pixel disk in bright magenta. The
            // UV→pixel mapping MUST match UvPngWriter — it renders the
            // [-0.1, 1.1] UV range (to show out-of-bounds verts around
            // the unit box), not [0,1] linearly. If we map dots [0,1] →
            // [0, size] the points land outside the chart region rendered
            // by the backdrop. Mirror the helper's UvLo / UvHi constants.
            // V-axis: UvPngWriter draws with GL.LoadPixelMatrix(0,size,0,size)
            // where screen Y=0 is bottom; ReadPixels copies that to texture
            // pixels[0..size-1], and EncodeToPNG writes pixels forward so
            // pixels[0] lands at the TOP of the displayed PNG. To stay aligned
            // with the backdrop, dots must use py = ny * size (NO extra flip)
            // — the earlier `1 - ny` mirrored every dot across the horizontal
            // midline, leaving fan-spokes pointing the wrong way on
            // asymmetric charts (visible on Gazebo's keystone shells).
            const float kUvLo = -0.1f, kUvHi = 1.1f;
            float uvRange = kUvHi - kUvLo;
            Color32 dot = new Color32(220, 30, 180, 255);
            foreach (var sm in r.proxySamples)
            {
                float nx = (sm.uv2.x - kUvLo) / uvRange;
                float ny = (sm.uv2.y - kUvLo) / uvRange;
                int px = Mathf.Clamp(Mathf.FloorToInt(nx * size), 1, size - 2);
                int py = Mathf.Clamp(Mathf.FloorToInt(ny * size), 1, size - 2);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    pixels[(py + dy) * size + (px + dx)] = dot;
            }

            string path = Path.Combine(outputDir, "proxy_samples.png");
            EncodePng(pixels, size, size, path);
        }

        // ─── PR-3 Stage 3: project proxy samples onto each fine LOD ───

        /// <summary>For every fine LOD: closest-point each proxy sample
        /// onto its mesh surface, bin hits per face. Stage 4 will read
        /// the per-face dominant proxy shell + hit count to fit a
        /// per-fine-shell affine (overlay candidate) or fall through to
        /// promote when coverage is sparse. Skipped for the deepest LOD
        /// (proxy ≡ deepest, no projection needed) and for LODs without
        /// a renderer.</summary>
        static void ProjectProxySamplesOntoFineLods(LODGroup lg, Options opts,
            float meshDiag, Result r)
        {
            if (r.proxySamples == null || r.proxySamples.Length == 0) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            int deepest = lodCount - 1;
            while (deepest > 0 && (lods[deepest].renderers == null
                || lods[deepest].renderers.Length == 0
                || lods[deepest].renderers[0] == null)) deepest--;

            r.lodProjections = new FineLodProjection[lodCount];
            float distAbsThreshold = opts.overlayDistNorm * meshDiag;

            for (int li = 0; li < lodCount; li++)
            {
                if (li == deepest) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var xform = rs[0].transform;

                var fineFaces = BuildFaceData(mesh, xform, meshDiag,
                    out Vector3[] fineWorldVerts, out int[] fineRawTris,
                    out _, out _);
                int faceCount = fineFaces.Length;
                if (faceCount == 0) continue;
                BuildDeepAabbs(fineWorldVerts, fineRawTris,
                    out var fineMin, out var fineMax);

                var proj = new FineLodProjection
                {
                    perFaceHitCount           = new int[faceCount],
                    perFaceDominantProxyShell = new int[faceCount],
                    perFaceAvgDist            = new float[faceCount],
                    perFaceHitSampleIdx       = new int[faceCount][],
                };
                for (int f = 0; f < faceCount; f++)
                    proj.perFaceDominantProxyShell[f] = -1;

                // Per-face tally of (sampleIdx, proxyShellId) hits. Sample
                // indices are persisted in proj.perFaceHitSampleIdx so
                // Stage 4 can re-walk them per fine shell without redoing
                // the closest-face search; shell IDs are kept locally for
                // the dominant-shell vote.
                var hitsByFace      = new List<int>[faceCount]; // sample indices
                var shellsByFace    = new List<int>[faceCount]; // proxy shell ids per hit

                for (int sIdx = 0; sIdx < r.proxySamples.Length; sIdx++)
                {
                    var sm = r.proxySamples[sIdx];
                    int closestFace = ProjectVertexToDeepMesh(sm.worldPos,
                        fineFaces, fineWorldVerts, fineRawTris,
                        fineMin, fineMax, out float dist);
                    if (closestFace < 0 || dist > distAbsThreshold)
                    {
                        proj.missedSamples++;
                        continue;
                    }
                    proj.perFaceHitCount[closestFace]++;
                    proj.perFaceAvgDist[closestFace] += dist;
                    if (hitsByFace[closestFace] == null)
                    {
                        hitsByFace[closestFace]   = new List<int>(4);
                        shellsByFace[closestFace] = new List<int>(4);
                    }
                    hitsByFace[closestFace].Add(sIdx);
                    shellsByFace[closestFace].Add(sm.proxyShellId);
                    proj.totalHits++;
                }

                // Resolve dominant proxy shell per face + finalize avg dist
                // + freeze the per-face sample list as an int[] (cheaper
                // than retaining the List<int> for the lifetime of Result).
                var shellTally = new Dictionary<int, int>();
                for (int f = 0; f < faceCount; f++)
                {
                    if (proj.perFaceHitCount[f] == 0) continue;
                    proj.perFaceAvgDist[f] /= proj.perFaceHitCount[f];
                    proj.perFaceHitSampleIdx[f] = hitsByFace[f].ToArray();
                    var shells = shellsByFace[f];
                    shellTally.Clear();
                    foreach (int sh in shells)
                    {
                        shellTally.TryGetValue(sh, out int v);
                        shellTally[sh] = v + 1;
                    }
                    int bestShell = -1, bestCount = 0;
                    foreach (var kv in shellTally)
                        if (kv.Value > bestCount) { bestCount = kv.Value; bestShell = kv.Key; }
                    proj.perFaceDominantProxyShell[f] = bestShell;
                }
                r.lodProjections[li] = proj;
            }
        }

        // ─── PR-3 Stage 5: ortho-project fine faces onto proxy faces ──

        /// <summary>For every fine LOD, project each fine face
        /// orthographically onto the nearest deepest-LOD (proxy) face in
        /// 3D, with normal-sign disambiguation so sym-split mirror twins
        /// pick the chart whose winding matches the fine face. Each fine
        /// vertex's UV is the barycentric pull of proxy_uv2 at the
        /// orthogonal projection point on the chosen proxy face plane —
        /// the fine LOD inherits the proxy's UV layout directly. No
        /// affine fits, no clamps, no chart wrapping. Per the
        /// architecture: 'orthographically project each finer LOD's
        /// faces into the LOD-deepest atlas regions by 3D nearest-face
        /// + normal-sign correspondence. UV2 overlaps inside a region
        /// are intentional (shared lighting domain).'
        /// Seam vertices on shared fine edges where each side picked a
        /// different proxy face get one output copy per (origVi, proxy
        /// face) — keeps every fine tri inside one proxy face's UV
        /// region, no inter-region tris.</summary>
        static void BuildFinalFineUv2(LODGroup lg, Options opts, Result r)
        {
            if (lg == null) return;
            if (r.proxyUv2 == null || r.proxyTris == null || r.proxyWorldVerts == null)
                return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            int deepest = lodCount - 1;
            while (deepest > 0 && (lods[deepest].renderers == null
                || lods[deepest].renderers.Length == 0
                || lods[deepest].renderers[0] == null)) deepest--;

            r.finalUv2 = new Vector2[lodCount][];
            r.finalTris = new int[lodCount][];
            r.finalSourceVertexIdx = new int[lodCount][];
            r.finalAtlasV = 1f;

            // Proxy face data: centroid, area, unit normal — and AABBs
            // for cheap rejection during closest-face search. Built once,
            // reused across every fine LOD.
            int proxyFaceN = r.proxyTris.Length / 3;
            if (proxyFaceN == 0) return;
            var proxyFaces = new Face3D[proxyFaceN];
            for (int f = 0; f < proxyFaceN; f++)
            {
                Vector3 a = r.proxyWorldVerts[r.proxyTris[f * 3]];
                Vector3 b = r.proxyWorldVerts[r.proxyTris[f * 3 + 1]];
                Vector3 c = r.proxyWorldVerts[r.proxyTris[f * 3 + 2]];
                proxyFaces[f].centroid = (a + b + c) / 3f;
                Vector3 cr = Vector3.Cross(b - a, c - a);
                float mag = cr.magnitude;
                proxyFaces[f].area   = mag * 0.5f;
                proxyFaces[f].normal = mag > 1e-12f ? cr / mag : Vector3.up;
            }
            BuildDeepAabbs(r.proxyWorldVerts, r.proxyTris, out var proxyMin, out var proxyMax);

            for (int li = 0; li < lodCount; li++)
            {
                if (li == deepest) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var xform = rs[0].transform;
                var localVerts = mesh.vertices;
                var tris = mesh.triangles;
                int faceCount = tris.Length / 3;
                if (faceCount == 0) continue;

                var worldVerts = new Vector3[localVerts.Length];
                for (int i = 0; i < localVerts.Length; i++)
                    worldVerts[i] = xform.TransformPoint(localVerts[i]);

                // (origVi, proxyFaceIdx) → newVi dedup. A fine vertex
                // shared by two fine faces that pick different proxy
                // faces gets one copy per proxy face so each fine face
                // stays inside one proxy face's UV region. Same proxy
                // face → same copy → seam shrinks to a 0-area pair on
                // identical UVs.
                var dedup   = new Dictionary<long, int>(localVerts.Length);
                var outUv   = new List<Vector2>(localVerts.Length * 2);
                var outTris = new int[tris.Length];
                var outSrc  = new List<int>(localVerts.Length * 2);
                int NewIndex(int origVi, int proxyFace)
                {
                    long key = ((long)(proxyFace + 1) << 32) | (uint)origVi;
                    if (dedup.TryGetValue(key, out int existing)) return existing;
                    int newIdx = outUv.Count;
                    outUv.Add(Vector2.zero);
                    outSrc.Add(origVi);
                    dedup[key] = newIdx;
                    return newIdx;
                }

                for (int f = 0; f < faceCount; f++)
                {
                    int ia = tris[f * 3];
                    int ib = tris[f * 3 + 1];
                    int ic = tris[f * 3 + 2];
                    Vector3 va = worldVerts[ia];
                    Vector3 vb = worldVerts[ib];
                    Vector3 vc = worldVerts[ic];
                    Vector3 centroid = (va + vb + vc) / 3f;
                    Vector3 fineCross = Vector3.Cross(vb - va, vc - va);
                    float fineCrossMag = fineCross.magnitude;
                    Vector3 fineNormal = fineCrossMag > 1e-12f
                        ? fineCross / fineCrossMag : Vector3.up;

                    // Two-pass closest-face search: first require
                    // normal-sign agreement (dot > 0); if nothing
                    // passes, accept any sign. Sym-split twins differ
                    // only by chart UV winding — picking the matching-
                    // sign twin lands the fine face on the right chart.
                    int pfMatch = ProjectFaceToProxy(
                        centroid, fineNormal, proxyFaces,
                        r.proxyWorldVerts, r.proxyTris,
                        proxyMin, proxyMax, /*requireNormalSign*/ true);
                    if (pfMatch < 0)
                        pfMatch = ProjectFaceToProxy(
                            centroid, fineNormal, proxyFaces,
                            r.proxyWorldVerts, r.proxyTris,
                            proxyMin, proxyMax, /*requireNormalSign*/ false);

                    if (pfMatch < 0)
                    {
                        // No proxy face at all -- shouldn't happen on a
                        // well-formed LODGroup, but if it does we park
                        // off-atlas so the bake doesn't pull garbage.
                        for (int k = 0; k < 3; k++)
                        {
                            int origVi = tris[f * 3 + k];
                            int newIdx = NewIndex(origVi, -1);
                            outTris[f * 3 + k] = newIdx;
                            outUv[newIdx] = new Vector2(-1f, -1f);
                        }
                        continue;
                    }

                    // Cache the proxy face data we'll use 3 times.
                    int pa = r.proxyTris[pfMatch * 3];
                    int pb = r.proxyTris[pfMatch * 3 + 1];
                    int pc = r.proxyTris[pfMatch * 3 + 2];
                    Vector3 A = r.proxyWorldVerts[pa];
                    Vector3 B = r.proxyWorldVerts[pb];
                    Vector3 C = r.proxyWorldVerts[pc];
                    Vector2 uvA = r.proxyUv2[pa];
                    Vector2 uvB = r.proxyUv2[pb];
                    Vector2 uvC = r.proxyUv2[pc];
                    Vector3 nProxy = proxyFaces[pfMatch].normal;

                    for (int k = 0; k < 3; k++)
                    {
                        int origVi = tris[f * 3 + k];
                        Vector3 wp = worldVerts[origVi];
                        // Orthographic projection onto proxy face's
                        // plane: P = wp - n · ((wp - A) · n).
                        Vector3 P = wp - nProxy * Vector3.Dot(wp - A, nProxy);
                        Vector3 v0 = B - A, v1 = C - A, v2 = P - A;
                        float d00 = Vector3.Dot(v0, v0);
                        float d01 = Vector3.Dot(v0, v1);
                        float d11 = Vector3.Dot(v1, v1);
                        float d20 = Vector3.Dot(v2, v0);
                        float d21 = Vector3.Dot(v2, v1);
                        float denom = d00 * d11 - d01 * d01;
                        Vector2 uv;
                        if (Mathf.Abs(denom) < 1e-12f) { uv = uvA; }
                        else
                        {
                            float bV = (d11 * d20 - d01 * d21) / denom;
                            float bW = (d00 * d21 - d01 * d20) / denom;
                            float bU = 1f - bV - bW;
                            uv = uvA * bU + uvB * bV + uvC * bW;
                        }
                        int newIdx = NewIndex(origVi, pfMatch);
                        outTris[f * 3 + k] = newIdx;
                        outUv[newIdx] = uv;
                        if (uv.y > r.finalAtlasV) r.finalAtlasV = uv.y;
                    }
                }

                r.finalUv2[li]             = outUv.ToArray();
                r.finalTris[li]            = outTris;
                r.finalSourceVertexIdx[li] = outSrc.ToArray();
            }
        }

        /// <summary>Closest proxy face by 3D distance to <paramref name="q"/>.
        /// If <paramref name="requireNormalSign"/>, only proxy faces with
        /// dot(fineNormal, proxyNormal) &gt; 0 are considered — that's
        /// how sym-split mirror twins are disambiguated. Returns -1 if
        /// nothing passes the filter.</summary>
        static int ProjectFaceToProxy(
            Vector3 q, Vector3 fineNormal, Face3D[] proxyFaces,
            Vector3[] proxyVerts, int[] proxyTris,
            Vector3[] aabbMin, Vector3[] aabbMax,
            bool requireNormalSign)
        {
            int closest = -1;
            float bestSq = float.MaxValue;
            for (int f = 0; f < proxyFaces.Length; f++)
            {
                if (proxyFaces[f].area <= 0f) continue;
                if (requireNormalSign &&
                    Vector3.Dot(fineNormal, proxyFaces[f].normal) <= 0f)
                    continue;
                if (SqDistToAabb(q, aabbMin[f], aabbMax[f]) >= bestSq) continue;
                Vector3 a = proxyVerts[proxyTris[f * 3]];
                Vector3 b = proxyVerts[proxyTris[f * 3 + 1]];
                Vector3 c = proxyVerts[proxyTris[f * 3 + 2]];
                Vector3 pt = ClosestPointOnTriangle(q, a, b, c);
                float dsq = (pt - q).sqrMagnitude;
                if (dsq < bestSq) { bestSq = dsq; closest = f; }
            }
            return closest;
        }

        /// <summary>PR-3 Stage 5 visualisation: render the final per-LOD
        /// UV2 layout — exactly what would be written to mesh.uv2. The
        /// yellow border drawn by UvPngWriter is the unit box, so any
        /// natural spill above V=1 (or below 0) shows up outside it.</summary>
        static void WriteFinalUv2Pngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.finalUv2 == null || r.finalTris == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            for (int li = 0; li < lodCount; li++)
            {
                var uv = r.finalUv2[li];
                var tr = r.finalTris[li];
                if (uv == null || tr == null || tr.Length < 3) continue;
                string path = Path.Combine(outputDir, $"lod{li}_final_uv2.png");
                UvPngWriter.Render(path, uv, tr);
            }
        }

        /// <summary>Render each fine LOD as a 3D isometric view with faces
        /// shaded by proxy-sample hit density. Heat ramp: pink (zero hits =
        /// fine has no proxy support → promote candidate) → light green
        /// (some hits) → saturated green (many hits = overlay candidate
        /// with strong proxy backing). The eye picks out promote zones
        /// (pink blobs) vs overlay zones (green) without staring at CSV
        /// numbers.</summary>
        static void WriteProxyHitsPngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.lodProjections == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            for (int li = 0; li < lodCount; li++)
            {
                var proj = r.lodProjections[li];
                if (proj.perFaceHitCount == null) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                var localVerts = mesh.vertices;
                var tris = mesh.triangles;
                int faceCount = tris.Length / 3;
                if (faceCount == 0 || faceCount != proj.perFaceHitCount.Length) continue;
                var xform = rs[0].transform;

                var worldVerts = new Vector3[localVerts.Length];
                Vector3 mn = xform.TransformPoint(localVerts[0]);
                Vector3 mx = mn;
                worldVerts[0] = mn;
                for (int i = 1; i < localVerts.Length; i++)
                {
                    var p = xform.TransformPoint(localVerts[i]);
                    worldVerts[i] = p;
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }

                // Saturate the ramp at the per-LOD 95th-percentile hit
                // count so a single hotspot doesn't wash out the rest.
                int maxHits = 0;
                for (int f = 0; f < faceCount; f++)
                    if (proj.perFaceHitCount[f] > maxHits) maxHits = proj.perFaceHitCount[f];
                int ramp = Mathf.Max(1, maxHits);

                Vector3 isoR = new Vector3( 0.7071f, 0f, -0.7071f);
                Vector3 isoU = new Vector3(-0.4082f, 0.8165f, -0.4082f);
                float umin = float.MaxValue, umax = float.MinValue;
                float vmin = float.MaxValue, vmax = float.MinValue;
                for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    Vector3 corner = new Vector3(
                        cx == 0 ? mn.x : mx.x,
                        cy == 0 ? mn.y : mx.y,
                        cz == 0 ? mn.z : mx.z);
                    float cu = Vector3.Dot(corner, isoR);
                    float cv = Vector3.Dot(corner, isoU);
                    if (cu < umin) umin = cu; if (cu > umax) umax = cu;
                    if (cv < vmin) vmin = cv; if (cv > vmax) vmax = cv;
                }
                int size = 1024;
                float du = umax - umin, dv = vmax - vmin;
                float scale = (size * 0.92f) / Mathf.Max(Mathf.Max(du, dv), 1e-6f);
                float midU = (umin + umax) * 0.5f, midV = (vmin + vmax) * 0.5f;
                float half = size * 0.5f;

                var pixels = new Color32[size * size];
                var bg = new Color32(244, 244, 248, 255);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

                for (int f = 0; f < faceCount; f++)
                {
                    Color32 col = HitColor(proj.perFaceHitCount[f], ramp);
                    Vector2 a = IsoProject(worldVerts[tris[f * 3]],     isoR, isoU, midU, midV, scale, half);
                    Vector2 b = IsoProject(worldVerts[tris[f * 3 + 1]], isoR, isoU, midU, midV, scale, half);
                    Vector2 c = IsoProject(worldVerts[tris[f * 3 + 2]], isoR, isoU, midU, midV, scale, half);
                    RasterizeTrianglePx(pixels, size, a, b, c, col);
                }

                string path = Path.Combine(outputDir, $"lod{li}_proxy_hits.png");
                EncodePng(pixels, size, size, path);
            }
        }


        /// <summary>Heatmap ramp: 0 hits → pink (no proxy support), then
        /// pale-green at low counts ramping to saturated green at <c>ramp</c>
        /// hits. Diverging palette so the operator sees overlay-friendly
        /// regions (green) vs detail-not-on-proxy regions (pink) at a
        /// glance, even on grayscale-blind monitors.</summary>
        static Color32 HitColor(int hits, int ramp)
        {
            if (hits <= 0) return new Color32(255, 120, 200, 255); // promote candidate
            float t = Mathf.Clamp01((float)hits / ramp);
            // Pale → saturated green.
            byte r = (byte)Mathf.Lerp(200f,  60f, t);
            byte g = (byte)Mathf.Lerp(240f, 170f, t);
            byte b = (byte)Mathf.Lerp(200f,  80f, t);
            return new Color32(r, g, b, 255);
        }

        /// <summary>Drive xatlasAddMesh + ComputeCharts + PackCharts on a
        /// raw 3D mesh (positions + normals + indices). Returns the packed
        /// per-output-vertex UV2 array and the corresponding output index
        /// buffer. Output index buffer may differ from mesh.triangles —
        /// xatlas can split vertices at chart seams.</summary>
        static void AutoUnwrapDeepMesh(Mesh mesh, Transform xform, Options opts,
            out Vector2[] outUv, out int[] outTris, out Vector3[] outWorldVerts)
        {
            outUv = null; outTris = null; outWorldVerts = null;
            var verts = mesh.vertices;
            var tris  = mesh.triangles;
            var normals = mesh.normals;
            int vc = verts.Length;
            int ic = tris.Length;
            if (vc == 0 || ic == 0) return;

            var positionsFlat = new float[vc * 3];
            for (int i = 0; i < vc; i++)
            {
                positionsFlat[i * 3 + 0] = verts[i].x;
                positionsFlat[i * 3 + 1] = verts[i].y;
                positionsFlat[i * 3 + 2] = verts[i].z;
            }
            float[] normalsFlat = null;
            if (normals != null && normals.Length == vc)
            {
                normalsFlat = new float[vc * 3];
                for (int i = 0; i < vc; i++)
                {
                    normalsFlat[i * 3 + 0] = normals[i].x;
                    normalsFlat[i * 3 + 1] = normals[i].y;
                    normalsFlat[i * 3 + 2] = normals[i].z;
                }
            }
            var indicesU = new uint[ic];
            for (int i = 0; i < ic; i++) indicesU[i] = (uint)tris[i];

            XatlasNative.xatlasCreate();
            try
            {
                int addErr = XatlasNative.xatlasAddMesh(positionsFlat, normalsFlat,
                    (uint)vc, indicesU, (uint)ic);
                if (addErr != 0)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] xatlasAddMesh err={addErr}");
                    return;
                }
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(
                    maxChartSize: 0,
                    padding: (uint)opts.interDomainPaddingPx,
                    texelsPerUnit: 0f,
                    resolution: (uint)opts.atlasResolutionPx,
                    bilinear: 1,
                    blockAlign: 0,
                    bruteForce: 1,
                    // 0 disables the 90° chart-rotation step xatlas runs by
                    // default for packing density. With rotation ON the
                    // diagonal triangulation inside each chart flips
                    // between variants — operator reads it as a "rotated
                    // UV layout". Keeping natural orientation costs ~5%
                    // packing efficiency but the diagnostic stays stable.
                    rotateCharts: 0,
                    rotateChartsToAxis: 0);

                int meshCount = XatlasNative.xatlasGetMeshCount();
                if (meshCount <= 0) return;
                int outVc = XatlasNative.xatlasGetOutputVertexCount(0);
                int outIc = XatlasNative.xatlasGetOutputIndexCount(0);
                if (outVc <= 0 || outIc <= 0) return;

                var xref = new uint[outVc];
                var uvFlat = new float[outVc * 2];
                var chartIdx = new uint[outVc];
                XatlasNative.xatlasGetOutputVertexData(0, xref, uvFlat, chartIdx, outVc);

                var outIndsU = new uint[outIc];
                XatlasNative.xatlasGetOutputIndices(0, outIndsU, outIc);

                outUv = new Vector2[outVc];
                outWorldVerts = new Vector3[outVc];
                for (int i = 0; i < outVc; i++)
                {
                    outUv[i] = new Vector2(uvFlat[i * 2], uvFlat[i * 2 + 1]);
                    // xref maps each output vert back to its original input vert,
                    // whose local position is mesh.vertices[xref[i]]. Apply the
                    // deepest LOD's xform once to land in world space.
                    int origIdx = (int)xref[i];
                    outWorldVerts[i] = xform.TransformPoint(verts[origIdx]);
                }
                outTris = new int[outIc];
                for (int i = 0; i < outIc; i++) outTris[i] = (int)outIndsU[i];
            }
            finally { XatlasNative.xatlasDestroy(); }
        }

        // ─── Diagnostic PNG output ───────────────────────────────────

        /// <summary>Render the packed atlas layout — each domain's uv2Rect
        /// drawn as a filled rectangle, colored by base vs promoted (blue
        /// vs warm), with per-domain hue variation so adjacent rects can be
        /// distinguished. Black 1px border separates rects. Output:
        /// <c>{outputDir}/atlas.png</c>.</summary>
        static void WriteAtlasPng(string outputDir, Result r)
        {
            if (r.domains == null || r.domains.Length == 0) return;
            const int size = 1024;
            var pixels = new Color32[size * size];
            // Dark backdrop so empty atlas area is visible.
            var bg = new Color32(24, 24, 28, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

            for (int i = 0; i < r.domains.Length; i++)
            {
                var d = r.domains[i];
                int x0 = Mathf.Clamp(Mathf.FloorToInt(d.uv2Rect.x * size), 0, size - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(d.uv2Rect.y * size), 0, size - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((d.uv2Rect.x + d.uv2Rect.width) * size), 0, size);
                int y1 = Mathf.Clamp(Mathf.CeilToInt((d.uv2Rect.y + d.uv2Rect.height) * size), 0, size);
                Color32 fill = DomainColor(i, r.baseShellCount);
                for (int y = y0; y < y1; y++)
                {
                    int row = y * size;
                    for (int x = x0; x < x1; x++) pixels[row + x] = fill;
                }
            }

            string path = Path.Combine(outputDir, "atlas.png");
            EncodePng(pixels, size, size, path);
        }

        /// <summary>Render every fine-LOD mesh's triangles, projected onto
        /// the world-space plane that shows the largest surface (axis with
        /// the smallest AABB extent → normal to the "best view" plane).
        /// Colored by faceToDomain assignment. Lets the operator SEE
        /// where overlay vs promote vs skip lands on the actual physical
        /// surface — red blobs in the middle of an obvious wall mean a
        /// topology divergence the per-vertex classifier flagged; red
        /// strips only along chart borders mean only boundary faces flunk
        /// face-consensus. Output: <c>{outputDir}/lod{N}.png</c>
        /// per fine LOD. Background is light so face colors pop; skip /
        /// degenerate faces render bright magenta so they're impossible
        /// to mistake for backdrop. Atlas-projection (UV0) is intentionally
        /// NOT used here — assets often pack UV0 into a corner sub-region,
        /// leaving the diagnostic 95% empty.</summary>
        static void WriteFineLodDomainPngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.faceToDomain == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            int deepest = lodCount - 1;
            while (deepest > 0 && (lods[deepest].renderers == null
                || lods[deepest].renderers.Length == 0
                || lods[deepest].renderers[0] == null)) deepest--;

            for (int li = 0; li < lodCount; li++)
            {
                if (li == deepest) continue; // only fine LODs are interesting
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var xform = rs[0].transform;
                var localVerts = mesh.vertices;
                if (localVerts == null || localVerts.Length == 0) continue;
                var tris = mesh.triangles;
                var f2d = r.faceToDomain[li];
                if (f2d == null || f2d.Length * 3 != tris.Length) continue;

                // World-space verts + AABB.
                var worldVerts = new Vector3[localVerts.Length];
                Vector3 mn = xform.TransformPoint(localVerts[0]);
                Vector3 mx = mn;
                worldVerts[0] = mn;
                for (int i = 1; i < localVerts.Length; i++)
                {
                    var p = xform.TransformPoint(localVerts[i]);
                    worldVerts[i] = p;
                    mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                }

                // Isometric projection — axonometric basis on the (1,1,1)
                // normal so three orthogonal box faces are visible. Beats
                // a single-axis planar projection for boxes (top-down shows
                // only 2 walls), gives an immediate 3D read of the mesh.
                Vector3 isoR = new Vector3( 0.7071f, 0f, -0.7071f);            // right axis
                Vector3 isoU = new Vector3(-0.4082f, 0.8165f, -0.4082f);       // up axis
                // Project the 8 AABB corners to size the viewport.
                float umin = float.MaxValue, umax = float.MinValue;
                float vmin = float.MaxValue, vmax = float.MinValue;
                for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                for (int cz = 0; cz < 2; cz++)
                {
                    Vector3 corner = new Vector3(
                        cx == 0 ? mn.x : mx.x,
                        cy == 0 ? mn.y : mx.y,
                        cz == 0 ? mn.z : mx.z);
                    float cu = Vector3.Dot(corner, isoR);
                    float cv = Vector3.Dot(corner, isoU);
                    if (cu < umin) umin = cu; if (cu > umax) umax = cu;
                    if (cv < vmin) vmin = cv; if (cv > vmax) vmax = cv;
                }
                int size = 1024;
                float du = umax - umin, dv = vmax - vmin;
                float scale = (size * 0.92f) / Mathf.Max(Mathf.Max(du, dv), 1e-6f);
                float midU = (umin + umax) * 0.5f, midV = (vmin + vmax) * 0.5f;
                float half = size * 0.5f;

                var pixels = new Color32[size * size];
                var bg = new Color32(244, 244, 248, 255); // light backdrop
                for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

                int faceCount = tris.Length / 3;
                for (int f = 0; f < faceCount; f++)
                {
                    int domain = f2d[f];
                    Color32 col = CategoryColor(domain, r.baseShellCount);
                    Vector2 a = IsoProject(worldVerts[tris[f * 3]],     isoR, isoU, midU, midV, scale, half);
                    Vector2 b = IsoProject(worldVerts[tris[f * 3 + 1]], isoR, isoU, midU, midV, scale, half);
                    Vector2 c = IsoProject(worldVerts[tris[f * 3 + 2]], isoR, isoU, midU, midV, scale, half);
                    RasterizeTrianglePx(pixels, size, a, b, c, col);
                }

                string path = Path.Combine(outputDir, $"lod{li}.png");
                EncodePng(pixels, size, size, path);
            }
        }

        static Vector2 IsoProject(Vector3 p, Vector3 isoR, Vector3 isoU,
            float midU, float midV, float scale, float half)
        {
            float pu = Vector3.Dot(p, isoR);
            float pv = Vector3.Dot(p, isoU);
            return new Vector2(half + (pu - midU) * scale,
                               half - (pv - midV) * scale);
        }

        /// <summary>Color palette for diagnostic PNGs. Skip / degenerate
        /// (-1) → dark gray. Base shells (idx &lt; baseShellCount) → green
        /// family. Promoted clusters (idx ≥ baseShellCount) → warm
        /// (orange-red) family. Within each family the hue varies by
        /// domain index hash so adjacent shells are distinguishable.</summary>
        // Solid category palette for per-LOD diagnostic PNGs — the question
        // there is "is this face overlay / promote / skip?" Per-domain hue
        // variation would create false visual differences between LODs that
        // actually classify the same.
        static readonly Color32 kColorOverlay   = new Color32( 80, 190,  90, 255);
        static readonly Color32 kColorPromote   = new Color32(220,  90,  70, 255);
        static readonly Color32 kColorSkipDegen = new Color32(255,  40, 200, 255);

        static Color32 CategoryColor(int domainIdx, int baseShellCount)
        {
            if (domainIdx < 0) return kColorSkipDegen;
            return domainIdx < baseShellCount ? kColorOverlay : kColorPromote;
        }

        // Per-domain palette for atlas-layout PNG — there each rect must be
        // visually distinct from its neighbours, so we keep the hash-based
        // hue variation but only within the green / warm families.
        static Color32 DomainColor(int domainIdx, int baseShellCount)
        {
            if (domainIdx < 0) return kColorSkipDegen;
            uint h = unchecked((uint)domainIdx * 2654435761u);
            int hueOffset = (int)(h % 60u);
            int valOffset = (int)((h >> 8) % 30u);
            bool isBase = domainIdx < baseShellCount;
            float hue, sat, val;
            if (isBase)
            {
                hue = (90f + hueOffset) / 360f;
                sat = 0.60f;
                val = 0.70f + valOffset / 200f;
            }
            else
            {
                hue = (hueOffset * 0.5f) / 360f;
                sat = 0.85f;
                val = 0.85f + valOffset / 300f;
            }
            Color c = Color.HSVToRGB(hue, sat, val);
            return new Color32(
                (byte)Mathf.Clamp(c.r * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.g * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.b * 255f, 0f, 255f), 255);
        }

        /// <summary>Software-rasterize a triangle given in pixel coordinates
        /// with a solid color. Edge-function barycentrics with positive-only
        /// inside test (top-left rule not enforced — diagnostic doesn't
        /// need pixel-perfect seam handling).</summary>
        static void RasterizeTrianglePx(Color32[] pix, int size,
            Vector2 a, Vector2 b, Vector2 c, Color32 col)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));

            float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denom) < 1e-6f) return;
            float invDenom = 1f / denom;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * size;
                for (int x = x0; x <= x1; x++)
                {
                    float w1 = ((b.y - c.y) * (x - c.x) + (c.x - b.x) * (y - c.y)) * invDenom;
                    float w2 = ((c.y - a.y) * (x - c.x) + (a.x - c.x) * (y - c.y)) * invDenom;
                    float w3 = 1f - w1 - w2;
                    if (w1 < 0f || w2 < 0f || w3 < 0f) continue;
                    pix[row + x] = col;
                }
            }
        }

        /// <summary>Encode a pixel buffer to PNG via a transient Texture2D.
        /// Caller owns the path and ensures the directory exists.</summary>
        static void EncodePng(Color32[] pixels, int width, int height, string path)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static void LogDryRunSummary(string lgName, Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[HierRepack] Dry-run on '{lgName}':");
            sb.AppendLine($"  domains:        {r.domains.Length} total " +
                $"({r.baseShellCount} base / {r.promotedClusterCount} promoted shells)");
            sb.AppendLine($"  atlas:          {r.atlasPixelWidth} × {r.atlasPixelHeight} px " +
                $"(naive strip packer — PR-2.8 will swap in xatlas)");
            int denom = Mathf.Max(1, r.totalFineFaces);
            sb.AppendLine($"  fine faces:     {r.totalFineFaces} total");
            sb.AppendLine($"    promoted:     {r.promotedFineFaces,6} ({100f * r.promotedFineFaces / denom,5:F1}%)");
            sb.AppendLine($"    overlaid:     {r.overlaidFineFaces,6} ({100f * r.overlaidFineFaces / denom,5:F1}%)");
            sb.AppendLine($"    skipped:      {r.skippedFineFaces,6} ({100f * r.skippedFineFaces / denom,5:F1}%)");
            sb.AppendLine($"    degenerate:   {r.degenerateFineFaces,6} (filtered before shell extraction)");

            // Per-LOD assignment audit — how many of each LOD's faces went where.
            for (int li = 0; li < r.faceToDomain.Length; li++)
            {
                var arr = r.faceToDomain[li];
                if (arr == null || arr.Length == 0) continue;
                int baseCount = 0, promCount = 0, miss = 0;
                for (int f = 0; f < arr.Length; f++)
                {
                    int idx = arr[f];
                    if (idx < 0) miss++;
                    else if (idx < r.baseShellCount) baseCount++;
                    else promCount++;
                }
                // base = native shell (deepest LOD) or overlaid onto a base
                // (fine LODs); promoted = own atlas domain; skip/degen = -1.
                sb.AppendLine($"    LOD{li}: {arr.Length,5} faces  " +
                    $"base/overlay={baseCount,5}  promoted={promCount,5}  skip/degen={miss}");
            }

            // Top-K largest and smallest domains by pixel area — useful for
            // sanity-checking the packer's distribution.
            int domainsToShow = Mathf.Min(5, r.domains.Length);
            var byArea = new int[r.domains.Length];
            for (int i = 0; i < r.domains.Length; i++) byArea[i] = i;
            Array.Sort(byArea, (a, b) =>
            {
                float aa = r.domains[a].uv2Rect.width * r.domains[a].uv2Rect.height;
                float ba = r.domains[b].uv2Rect.width * r.domains[b].uv2Rect.height;
                return ba.CompareTo(aa);
            });
            sb.AppendLine($"  Top {domainsToShow} largest domains:");
            for (int i = 0; i < domainsToShow; i++)
            {
                var d = r.domains[byArea[i]];
                sb.AppendLine($"    #{byArea[i]:D4} {(d.isPromoted ? "PROM" : "BASE")} " +
                    $"rect=({d.uv2Rect.x:F3},{d.uv2Rect.y:F3})-" +
                    $"({d.uv2Rect.x + d.uv2Rect.width:F3},{d.uv2Rect.y + d.uv2Rect.height:F3})  " +
                    $"faces={d.faceCount} area3D={d.totalArea3D:F3} " +
                    $"sourceLod={d.sourceLodIndex}");
            }

            // Stage 5: max V reached by the natural ortho-projection.
            // Per the architecture, fine vertices CAN spill outside the
            // proxy chart — we do not distort. >1 just means at least
            // one fine face's projection landed above the unit box.
            if (r.finalUv2 != null)
            {
                sb.AppendLine($"  final UV2 max V: {r.finalAtlasV:F3} " +
                    (r.finalAtlasV > 1f + 1e-4f
                        ? "(natural spill above unit box — expected, not distorted)"
                        : "(within unit box)"));
            }
            UvtLog.Info(UvtLog.Category.Benchmark, sb.ToString());
        }

        // Default path used by stand-alone callers (none remain in this PR,
        // kept for future ad-hoc invocations).
        static string WriteDryRunReport(string lgName, Result r)
            => WriteDryRunReport(lgName, r, null);

        /// <summary>Write the per-case dry-run CSV. If <paramref name="outputDir"/>
        /// is non-null the file lands as <c>{outputDir}/repack.csv</c>
        /// (subfolder identifies the technique); otherwise falls back to the
        /// timestamped BenchmarkReports/ layout used by stand-alone callers.</summary>
        static string WriteDryRunReport(string lgName, Result r, string outputDir)
        {
            string dir;
            string path;
            if (!string.IsNullOrEmpty(outputDir))
            {
                dir = outputDir;
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "repack.csv");
            }
            else
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                     ?? Application.dataPath;
                dir = Path.Combine(projectRoot, "BenchmarkReports");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                path = Path.Combine(dir, $"hierrepack_{stamp}_{Sanitize(lgName)}.csv");
            }
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("domainIdx,isPromoted,sourceLod,faceCount,totalArea3D," +
                "uvRectX,uvRectY,uvRectW,uvRectH," +
                "planeCx,planeCy,planeCz,planeNx,planeNy,planeNz," +
                "extentU,extentV");
            for (int i = 0; i < r.domains.Length; i++)
            {
                var d = r.domains[i];
                sb.Append(i.ToString(inv)).Append(',');
                sb.Append(d.isPromoted ? '1' : '0').Append(',');
                sb.Append(d.sourceLodIndex.ToString(inv)).Append(',');
                sb.Append(d.faceCount.ToString(inv)).Append(',');
                sb.Append(d.totalArea3D.ToString("R", inv)).Append(',');
                sb.Append(d.uv2Rect.x.ToString("R", inv)).Append(',');
                sb.Append(d.uv2Rect.y.ToString("R", inv)).Append(',');
                sb.Append(d.uv2Rect.width.ToString("R", inv)).Append(',');
                sb.Append(d.uv2Rect.height.ToString("R", inv)).Append(',');
                sb.Append(d.planeCentroid.x.ToString("R", inv)).Append(',');
                sb.Append(d.planeCentroid.y.ToString("R", inv)).Append(',');
                sb.Append(d.planeCentroid.z.ToString("R", inv)).Append(',');
                sb.Append(d.planeNormal.x.ToString("R", inv)).Append(',');
                sb.Append(d.planeNormal.y.ToString("R", inv)).Append(',');
                sb.Append(d.planeNormal.z.ToString("R", inv)).Append(',');
                sb.Append(d.planeExtentU.ToString("R", inv)).Append(',');
                sb.Append(d.planeExtentV.ToString("R", inv));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
