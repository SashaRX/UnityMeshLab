// HierarchicalRepack.cs — builder for the hierarchical UV2 atlas
// (cascade plan v2 — group-then-final-pack).
//
// CURRENT STATE: legacy PR-2 single-proxy / promote-overlay classifier
// and the PR-3 single-proxy projector have both been removed. The
// surviving Build() runs only Stage 1 (proxy unwrap variants) +
// Stage B (per-LOD classical unwrap diagnostic) + Stage 2 (Poisson
// samples on the deepest LOD) + Stage 3 (per-fine-LOD sample projection
// bookkeeping). Stage C (per-LOD 3D shell extract + group seed),
// Stage D (cascade pairwise grouping deep→fine), Stage E (final pack
// of all canonical charts + per-LOD uv2), Stage F (apply + bake)
// come next per Documentation~/HIERARCHICAL_CASCADE_PLAN.md.
//
// PUBLIC API still exposed:
//   • Build(LODGroup, Options) → Result
//   • BuildAndWriteForCase(LODGroup, Options, string) → Result
//   • BuildFinalMeshes(LODGroup, Result)  (consumes finalUv2/Tris/
//     SourceVertexIdx — currently null until Stage E re-populates)
//
// Public types kept for cross-stage data flow; legacy fields
// (LightingDomain, PromotedCluster, faceToDomain, baseShellCount,
// atlasPixelWidth/Height, totalFineFaces/promotedFineFaces/...) are
// gone.

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

            /// <summary>Stage D tiny-shell merge threshold (sweep 2026-06-03).
            /// A finer-LOD shell whose <c>totalArea ≤ skipAreaFrac × totalFineArea</c>
            /// AND <c>faceCount ≤ skipMaxFaceCount</c> is force-joined to its
            /// modal proxy parent even when the regular matchedFrac/minHits
            /// gate fails, because it's almost certainly thin trim / wire /
            /// fastener noise — the dominant source of fresh-group explosion
            /// on detail-heavy assets (Carousel 269→922 without this; Stage E
            /// would waste atlas space on every single piece of wire). If the
            /// tiny shell has no proxy match at all (bestProxy &lt; 0) it
            /// still opens a fresh group; counted as <c>tinyOrphan</c>.</summary>
            public float skipAreaFrac;

            /// <summary>Companion to <see cref="skipAreaFrac"/> — gate is AND,
            /// so a shell needs to be both area-small AND face-count-small to
            /// qualify as "tiny". Stops the rule from merging long thin
            /// strips (small area, many faces) that genuinely deserve their
            /// own domain.</summary>
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

            /// <summary>Stage D cascade-match threshold: a finer-LOD shell
            /// joins its deeper-LOD parent's lighting-domain group when the
            /// MODAL deeper shell's hit fraction (bestProxyHits / totalShellHits)
            /// is ≥ this. Below → the shell opens a fresh group (it's detail
            /// the deeper LOD lacks). Lower = more aggressive merging (risks
            /// lighting bleed across domains); higher = more fresh groups
            /// (risks atlas waste / over-segmentation). Sweep this with
            /// <see cref="cascadeMinHits"/> to find the knee per asset class.</summary>
            public float cascadeMatchFrac;

            /// <summary>Stage D cascade-match gate: a finer-LOD shell needs at
            /// least this many total projected sample hits before its modal
            /// vote is trusted to join a parent group. Below → fresh group
            /// regardless of fraction (too few samples = noisy vote, usually
            /// a sliver shell). Pairs with <see cref="cascadeMatchFrac"/>.</summary>
            public int cascadeMinHits;

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
                cascadeMatchFrac        = 0.5f,
                cascadeMinHits          = 4,
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

        public class Result
        {
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
            /// <summary>PR-3 Stage 6: cloned fine-LOD meshes with the
            /// new uv2 baked in (and vertex attributes duplicated to
            /// match the seam-vertex split). Indexed by LOD; null for
            /// the deepest LOD and for LODs whose projection failed.
            /// Built on demand by <see cref="BuildFinalMeshes"/>; the
            /// menu-driven Apply step swaps these into the renderers'
            /// MeshFilters via Undo.</summary>
            public Mesh[] finalMeshes;
            /// <summary>PR-3 Stage B: classical xatlas unwrap (sym-split
            /// + ARAP + pack) of each FINE LOD independently. Indexed
            /// by LOD; null for deepest and for LODs without UV0.
            /// Diagnostic-only at this stage -- Stage D's cascade
            /// projection will read these to seed unmatched-shell
            /// repacking. PNG output: lod{N}_classical_uv2.png.
            /// </summary>
            public Vector2[][] fineClassicalUv2;
            /// <summary>PR-3 Stage B: triangle index buffer for the
            /// classical unwrap above. Indexed by LOD. xatlas may split
            /// vertices at chart seams so this differs from the source
            /// mesh.triangles. Pair with fineClassicalUv2.</summary>
            public int[][]     fineClassicalTris;

            // ─── Stage C: per-LOD 3D shells + group seed ────────────
            /// <summary>Stage C: 3D shells extracted per LOD via
            /// ExtractShells on each LOD's geometry. Indexed by LOD
            /// level (matches LODGroup.GetLODs()). Each LOD has its
            /// own shell set — same params (shellNormalThresholdDeg)
            /// across LODs so shells on geometrically-equivalent LODs
            /// come out comparable.</summary>
            public Shell3D[][] perLodShells;
            /// <summary>Stage C: face → shellId per LOD. -1 for
            /// degenerate faces filtered out by ExtractShells. Pair
            /// with <see cref="perLodShells"/>.</summary>
            public int[][]     perLodFaceToShell;
            /// <summary>Stage C / Stage D: lighting domain groups.
            /// Stage C seeds one group per deepest-LOD shell. Stage D
            /// joins finer-LOD shells into existing groups (cascade
            /// matching) or creates new groups for shells with no
            /// match. Stage E packs canonical shells into the final
            /// atlas.</summary>
            public LightingDomainGroup[] groups;
            /// <summary>Stage C / Stage D: per-LOD shell → group id.
            /// Stage C fills the deepest LOD with 0..N-1 (one group
            /// per shell). Stage D fills the rest via cascade voting;
            /// -1 means the shell has no group yet (unmatched, Stage
            /// D will assign a fresh group).</summary>
            public int[][]     perLodShellToGroup;
            /// <summary>Stage D bookkeeping: one entry per LOD transition the
            /// cascade walked (deepest→0), in walk order. Drives the
            /// stage_d_sweep.csv diagnostic so threshold sweeps can be scored
            /// by join/new ratios without re-deriving from perLodShellToGroup.
            /// Null until Stage D runs.</summary>
            public List<CascadeStat> cascadeStats;

            // ─── Stage E: packed lighting-domain atlas ──────────────
            /// <summary>Stage E (slice E1): packed UV of the canonical
            /// charts — one chart per lighting-domain group, each group's
            /// canonical (deepest-member) shell projected onto its own
            /// plane and laid out by xatlas. Normalised [0,1]. Diagnostic
            /// render: domains_atlas.png. Null until Stage E runs.</summary>
            public Vector2[] domainAtlasUv;
            /// <summary>Stage E (slice E1): index buffer for
            /// <see cref="domainAtlasUv"/>. xatlas may split vertices at
            /// chart seams, so this is its own buffer.</summary>
            public int[] domainAtlasTris;
            /// <summary>Stage E (slice E1): packed atlas rect (normalised
            /// [0,1]) of each lighting-domain group, indexed by groupId.
            /// This is the SHARED layout: slice E2 maps every LOD's member
            /// shells into their group's rect so the whole lighting domain
            /// occupies the same atlas region across all LODs. Null entries
            /// (zero-size rect) mean the group contributed no packable
            /// geometry.</summary>
            public Rect[] domainAtlasRects;
            /// <summary>Stage E: per-group affine that maps a point's
            /// canonical-plane coordinate (in WORLD units: dot(worldPos −
            /// canonCentroid, canonBasisU/V)) to packed atlas UV —
            /// <c>uv = (su·inU + ou, sv·inV + ov)</c>. Fitted from the
            /// canonical chart's placed UV, so the canonical reproduces its
            /// own placement and every finer member of the group reuses the
            /// SAME affine → all members align in the same atlas region at the
            /// SAME texel density (xatlas packed at a fixed texelsPerUnit, so
            /// su≈sv is uniform across groups). Replaces the old per-shell
            /// [0,1] normalisation, which distorted non-square shells and gave
            /// each shell a different texel density. Indexed by groupId.</summary>
            public DomainPlacement[] domainPlacements;

            // ─── Stage E2/E3: per-face provenance + atlas metrics ────
            /// <summary>Stage E2: per LOD, per EMITTED face → Stage C shell
            /// id (parallel to finalTris/3). Lets Stage E3 tell legitimate
            /// same-shell seam ties from genuine UV overlaps.</summary>
            public int[][] finalFaceShell;
            /// <summary>Stage E2: per LOD, per emitted face → lighting-domain
            /// group id (-1 when the shell had no group).</summary>
            public int[][] finalFaceGroup;
            /// <summary>Stage E2: per LOD, faces emitted with the uv2 (0,0)
            /// fallback because their group had no valid placement. Non-zero
            /// means Stage E1 has gaps worth investigating.</summary>
            public int[] finalUnplacedFaces;
            /// <summary>Stage E3: objective per-LOD atlas quality metrics —
            /// the scalars threshold sweeps optimise and Apply reports.
            /// Indexed by LOD; default entries where the LOD produced no
            /// final UV2. CSV: stage_e_metrics.csv.</summary>
            public StageEMetrics[] stageEMetrics;
            /// <summary>Stage E3: per-LOD coverage/overlap raster
            /// (stageEMetricsRes² Color32) — grey covered, red overlapping,
            /// near-black empty. PNG: lod{N}_overlap.png.</summary>
            public Color32[][] stageEOverlapPx;
            /// <summary>Stage E3: side length of the metric raster.</summary>
            public int stageEMetricsRes;

            public string error;
        }

        /// <summary>Stage D per-transition counters for one (proxy, fine)
        /// LOD pair. Logged live and persisted on <see cref="Result.cascadeStats"/>
        /// for the threshold sweep CSV.</summary>
        public struct CascadeStat
        {
            public int liProxy;
            public int liFine;
            public int samples;
            public int hits;
            public int missed;
            /// <summary>Finer shells that inherited a parent group.</summary>
            public int joined;
            /// <summary>Finer shells that opened a fresh group (no match).</summary>
            public int fresh;
            /// <summary>Finer shells already assigned by an earlier
            /// (deeper) transition — skipped this pass.</summary>
            public int reused;
            /// <summary>Tiny shells (area &lt;= skipAreaFrac × totalFine AND
            /// faceCount &lt;= skipMaxFaceCount) that failed the regular
            /// matchedFrac/minHits test but had a resolvable parent, so
            /// they were force-joined instead of opening a fresh group.
            /// Sweep 2026-06-03 showed these are the dominant source of
            /// fresh-group explosion on detail-heavy assets (thin trim,
            /// wire, fasteners).</summary>
            public int tinyJoined;
            /// <summary>Tiny shells with no proxy match at all (deeper LOD
            /// genuinely lacks the geometry) — they still open fresh
            /// groups; subset of <see cref="fresh"/>.</summary>
            public int tinyOrphan;
        }

        /// <summary>Stage E: affine map from a canonical-plane coordinate (world
        /// units) to packed atlas UV, per lighting-domain group. See
        /// <see cref="Result.domainPlacements"/>.</summary>
        public struct DomainPlacement
        {
            public Vector2 uvc;    // canonical UV0-island centroid
            public float scale;    // uniform scale: in = (uv0 − uvc)·scale
            public float su, ou;   // atlasU = su·in.x + ou
            public float sv, ov;   // atlasV = sv·in.y + ov
            public bool valid;
        }

        /// <summary>Stage E3: per-LOD atlas quality metrics measured on the
        /// final cascaded UV2 (texel-centre raster at the atlas resolution).
        /// See <see cref="Result.stageEMetrics"/>; persisted to
        /// stage_e_metrics.csv by the benchmark.</summary>
        public struct StageEMetrics
        {
            public int lod;
            /// <summary>Faces in the source LOD mesh — compare with
            /// <see cref="faces"/> to spot silently dropped geometry.</summary>
            public int srcFaces;
            /// <summary>Faces emitted into finalUv2/finalTris.</summary>
            public int faces;
            /// <summary>Faces emitted with the uv2 (0,0) placement fallback.</summary>
            public int unplacedFaces;
            /// <summary>~Zero UV-area faces — not rasterised, no density sample.</summary>
            public int degenUvFaces;
            /// <summary>Faces with negative (flipped) UV winding.</summary>
            public int invertedFaces;
            /// <summary>Output verts outside [0,1] by more than half a texel.</summary>
            public int oobVerts;
            public int coveredTexels;
            /// <summary>coveredTexels / res² × 100.</summary>
            public float utilizationPct;
            /// <summary>Texels claimed by 2+ triangles that are neither the
            /// same face nor same-shell seam-adjacent — the direct
            /// lightmap-bleed proxy (UV0 mirror-reuse, residual folds).</summary>
            public int overlapTexels;
            public float overlapPctOfCovered;
            /// <summary>Distinct cross-shell pairs in conflict (capped 4096).</summary>
            public int overlapShellPairs;
            /// <summary>Area-derived texels-per-world-unit (√(Σtexel²/Σarea3D)).</summary>
            public float tpuMean;
            /// <summary>Area-weighted 1st / 99th percentile texel density.</summary>
            public float tpuP1, tpuP99;
            /// <summary>tpuP99 / tpuP1 — 1.0 = perfectly uniform.</summary>
            public float tpuSpread;
            /// <summary>Texels owned by groups whose canonical lives on a
            /// DIFFERENT LOD — the population the cascade promises to align.</summary>
            public int xLodTexels;
            /// <summary>% of <see cref="xLodTexels"/> that fall inside the
            /// canonical LOD's footprint for the same group (3×3 dilated).
            /// Low = the asset violates the shared-UV0-layout assumption →
            /// one bake would NOT be valid across LODs.</summary>
            public float xLodContainedPct;
            /// <summary>Groups with ≥16 texels at this LOD and &lt;50%
            /// containment — concrete cross-LOD alignment failures.</summary>
            public int misalignedGroups;
        }

        /// <summary>Stage C / D / E: a single lighting domain — a set
        /// of shells across multiple LODs that share lighting. Owned
        /// by its <c>canonicalLod</c> shell (the deepest-LOD member),
        /// whose geometry + UV parameterisation are the chart that
        /// Stage E packs into the final atlas. Finer members are
        /// projected onto the canonical member's plane and inherit
        /// its placed UV by barycentric pull.</summary>
        public struct LightingDomainGroup
        {
            public int groupId;
            /// <summary>Deepest LOD level that has a shell in this
            /// group. The canonical chart for Stage E packing.</summary>
            public int canonicalLod;
            /// <summary>Shell id (index into <c>perLodShells[canonicalLod]</c>)
            /// of the canonical member.</summary>
            public int canonicalShellId;
            /// <summary>All (lod, shellId) members of the group.
            /// Includes the canonical member as the first entry.
            /// Stage D appends finer members during cascade matching.
            /// </summary>
            public List<(int lod, int shellId)> members;
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

            // Mesh diagonal — reference scale for sampling density,
            // distance thresholds and canonical-vertex deduplication.
            // Legacy PR-2 deepest-LOD shell extraction / promote-overlay
            // classifier was removed; cascade architecture (plan v2)
            // does per-LOD shell work inside its own stages.
            float meshDiag = ComputeMeshDiagonal(meshes[deepest], xforms[deepest]);
            if (meshDiag < 1e-6f) meshDiag = 1f;

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
            // PR-3 Stage B: classical xatlas unwrap of each non-deepest
            // LOD independently. Diagnostic only at this stage; Stage D
            // will use these to seed the cascade repack.
            try { ComputeFineClassicalUnwraps(lg, opts, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] fine classical unwrap stage B failed on '{lg.name}': {ex.Message}");
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
            // Stage C: per-LOD 3D shell extract + seed deepest-LOD
            // groups. No matching / no UV here — only segmentation +
            // data-model seed. Stage D consumes perLodShells +
            // perLodShellToGroup + groups.
            try { ExtractPerLodShellsAndSeedGroups(lg, opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] stage C shells/groups failed on '{lg.name}': {ex.Message}");
            }
            // Stage D: cascade grouping deep→fine. Walks each (li_proxy,
            // li_proxy-1) transition, samples the deeper LOD's surface
            // tagged with the deeper LOD's shell id, projects samples onto
            // the finer LOD via closest-face, and votes per finer shell
            // whether to join the parent's group (matchedFrac >= 0.5) or
            // open a new group. Membership only — UV is Stage E's job.
            try { CascadeGroupShells(lg, opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] stage D cascade grouping failed on '{lg.name}': {ex.Message}");
            }
            // Stage E (slice E1): parameterise each lighting-domain group's
            // canonical shell planarly and pack the charts into one shared
            // atlas via xatlas (faceMaterial = groupId forces a chart
            // boundary per group). Produces the per-group atlas rects that
            // slice E2 will map every LOD's member shells into. Layout only
            // — no per-LOD UV2 / mesh writing here.
            try { PackDomainCharts(lg, opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] stage E domain pack failed on '{lg.name}': {ex.Message}");
            }
            // Stage E (slice E2): the cascade — every LOD's shells project into
            // their group's atlas rect so matched shells share the region
            // across LODs (one bake valid everywhere) and unmatched shells use
            // their repacked slot. Produces per-LOD finalUv2/finalTris/
            // finalSourceVertexIdx.
            try { BuildCascadedUv2(lg, opts, meshDiag, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] stage E cascade uv2 failed on '{lg.name}': {ex.Message}");
            }
            // Stage E (slice E3): objective atlas quality metrics — overlap
            // texels/pairs, inverted/degenerate faces, texel-density spread,
            // cross-LOD containment. The scalar that threshold sweeps
            // optimise and the reliability gate the Apply menu reports.
            try { ComputeStageEMetrics(lg, opts, result); }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] stage E3 metrics failed on '{lg.name}': {ex.Message}");
            }
            return result;
        }

        // ─── Internal types ──────────────────────────────────────────

        struct Face3D
        {
            public Vector3 centroid;
            public Vector3 normal;     // unit
            public float   area;
        }

        public struct Shell3D
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
        /// skip them via a sentinel marker instead of producing zero-area
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
            // Diagnostic PNG outputs that survived the legacy purge:
            //  • Stage 1   — proxy_uv2_clean/raw/auto.png
            //  • Stage B   — lodN_classical_uv2.png (per fine LOD)
            //  • Stage 2/3 — proxy_samples.png + lodN_proxy_hits.png
            // Stage 5 single-proxy final_uv2.png, atlas.png, lodN.png
            // (legacy domain category map) and the dry-run CSV writer
            // were dependencies of the deleted PR-2 pipeline. They will
            // come back during plan v2's Stage C/D/E with new layouts.
            WriteProxyUv2Png(outputDir, result);
            WriteFineClassicalUv2Pngs(outputDir, lg, result);
            WriteProxySamplesPng(outputDir, result);
            WriteProxyHitsPngs(outputDir, lg, result);
            // Stage C: per-LOD 3D shell partition iso views.
            WritePerLodShellsPngs(outputDir, lg, result);
            // Stage D: per-LOD lighting-domain groups (same colour across
            // LODs when cascade matched; new colours for unmatched detail).
            WritePerLodGroupsPngs(outputDir, lg, result);
            // Stage E (slice E1): the packed shared domain atlas — one chart
            // per lighting-domain group, laid out by xatlas.
            WriteDomainAtlasPng(outputDir, result);
            // Stage E (slice E2): per-LOD cascaded uv2 — same domain → same
            // atlas region across LODs. lod{N}_final_uv2.png.
            WriteFinalUv2Pngs(outputDir, lg, result);
            // Stage E (slice E3): objective metrics CSV + coverage/overlap
            // rasters (stage_e_metrics.csv, lod{N}_overlap.png).
            WriteStageEMetricsCsv(outputDir, result);
            WriteStageEOverlapPngs(outputDir, result);
            return result;
        }

        /// <summary>Stage D threshold sweep — for each
        /// (matchFrac × minHits) grid cell, rebuild the LODGroup with those
        /// cascade thresholds and append one <c>stage_d_sweep.csv</c> row
        /// per LOD transition per cell. No auto-winner — Stage E (which
        /// would expose a lightmap-defect scalar) isn't built yet, so the
        /// CSV's join/new/tiny ratios are the signal.
        ///
        /// The CSV is the deliverable. Per-cell group iso-view PNGs
        /// (<c>lod{N}_groups_mf{F}_mh{H}.png</c>) are written ONLY when
        /// <paramref name="emitPerCellPngs"/> is true — the first sweep run
        /// showed those PNGs are near-identical across cells on the major
        /// surfaces (variation is sub-pixel trim only), so by default they're
        /// suppressed as noise. The canonical visual is the baseline
        /// <c>lod{N}_groups.png</c> the hierarchicalRepack technique already
        /// writes at the default thresholds. Flip the flag for a deep visual
        /// dive (cells × LODs PNGs).
        ///
        /// Each cell is a full <see cref="Build"/> on a fresh
        /// <see cref="Result"/>, so there is zero cross-cell contamination;
        /// the cost is that the cascade-independent stages (xatlas unwraps,
        /// sampling) re-run per cell — acceptable for an opt-in diagnostic on
        /// a small grid. Returns the last cell's Result (or the error Result
        /// if the very first cell failed).</summary>
        public static Result BuildStageDSweep(LODGroup lg, Options baseOpts,
            float[] matchFracGrid, int[] minHitsGrid, string outputDir,
            bool emitPerCellPngs = false)
        {
            if (matchFracGrid == null || matchFracGrid.Length == 0)
                matchFracGrid = new[] { baseOpts.cascadeMatchFrac };
            if (minHitsGrid == null || minHitsGrid.Length == 0)
                minHitsGrid = new[] { baseOpts.cascadeMinHits };

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("matchFrac,minHits,groupCount,seedGroups,"
                + "liProxy,liFine,samples,hits,missed,joined,fresh,reused,"
                + "tinyJoined,tinyOrphan");

            Result last = null;
            foreach (float mf in matchFracGrid)
            foreach (int mh in minHitsGrid)
            {
                var opts = baseOpts;
                opts.cascadeMatchFrac = mf;
                opts.cascadeMinHits   = mh;

                Result r;
                try { r = Build(lg, opts); }
                catch (Exception ex)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] Stage D sweep cell mf={mf:F2} mh={mh} "
                        + $"on '{lg.name}' threw: {ex.Message}");
                    continue;
                }
                last = r;
                if (!string.IsNullOrEmpty(r.error))
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] Stage D sweep cell mf={mf:F2} mh={mh} "
                        + $"on '{lg.name}': {r.error}");
                    continue;
                }

                // Per-cell PNGs are opt-in noise (see method summary) — the
                // CSV is the real output. Suffix: period → 'p' so the
                // extension split stays unambiguous (lod0_groups_mf0p50_mh4.png).
                if (emitPerCellPngs)
                {
                    string suffix = $"_mf{mf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', 'p')}_mh{mh}";
                    WritePerLodGroupsPngs(outputDir, lg, r, suffix);
                }

                int groupCount = r.groups?.Length ?? 0;
                int seedGroups = (r.cascadeStats != null && r.cascadeStats.Count > 0)
                    ? groupCount - SumFresh(r.cascadeStats)  // groups present before any fresh adds
                    : groupCount;
                if (r.cascadeStats != null)
                    foreach (var st in r.cascadeStats)
                        csv.AppendLine(
                            $"{mf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},"
                            + $"{mh},{groupCount},{seedGroups},"
                            + $"{st.liProxy},{st.liFine},{st.samples},{st.hits},"
                            + $"{st.missed},{st.joined},{st.fresh},{st.reused},"
                            + $"{st.tinyJoined},{st.tinyOrphan}");
                else
                    csv.AppendLine(
                        $"{mf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},"
                        + $"{mh},{groupCount},{seedGroups},,,,,,,,,,");
            }

            try
            {
                File.WriteAllText(Path.Combine(outputDir, "stage_d_sweep.csv"),
                    csv.ToString());
            }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage D sweep CSV write failed on '{lg.name}': {ex.Message}");
            }

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[HierRepack] Stage D sweep '{lg.name}': "
                + $"{matchFracGrid.Length}×{minHitsGrid.Length} cells → stage_d_sweep.csv"
                + (emitPerCellPngs ? " + lod*_groups_mf*_mh*.png" : " (per-cell PNGs suppressed)"));
            return last;
        }

        static int SumFresh(List<CascadeStat> stats)
        {
            int n = 0;
            foreach (var s in stats) n += s.fresh;
            return n;
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
        /// <summary>Stage E (slice E1) diagnostic: render the packed shared
        /// domain atlas (one chart per lighting-domain group) to
        /// domains_atlas.png via the shared UV writer. UV is already [0,1].</summary>
        static void WriteDomainAtlasPng(string outputDir, Result r)
        {
            if (r.domainAtlasUv == null || r.domainAtlasTris == null) return;
            UvPngWriter.Render(Path.Combine(outputDir, "domains_atlas.png"),
                r.domainAtlasUv, r.domainAtlasTris);
        }

        /// <summary>Stage E (slice E2) diagnostic: render each LOD's cascaded
        /// uv2 to lod{N}_final_uv2.png. Same lighting domain → same atlas
        /// region across LODs, so the colour/region of a domain should line up
        /// from LOD0 down to the deepest.</summary>
        static void WriteFinalUv2Pngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.finalUv2 == null || r.finalTris == null) return;
            for (int li = 0; li < r.finalUv2.Length; li++)
            {
                if (r.finalUv2[li] == null || r.finalTris[li] == null) continue;
                UvPngWriter.Render(Path.Combine(outputDir, $"lod{li}_final_uv2.png"),
                    r.finalUv2[li], r.finalTris[li]);
            }
        }

        /// <summary>Stage E3: one CSV row per LOD with the atlas quality
        /// metrics — stage_e_metrics.csv (InvariantCulture).</summary>
        static void WriteStageEMetricsCsv(string outputDir, Result r)
        {
            if (r.stageEMetrics == null) return;
            var csv = new StringBuilder();
            csv.AppendLine("lod,srcFaces,faces,unplacedFaces,degenUvFaces,invertedFaces,"
                + "oobVerts,coveredTexels,utilPct,overlapTexels,overlapPctOfCovered,"
                + "overlapShellPairs,tpuMean,tpuP1,tpuP99,tpuSpread,"
                + "xLodTexels,xLodContainedPct,misalignedGroups");
            var ci = CultureInfo.InvariantCulture;
            foreach (var m in r.stageEMetrics)
                csv.AppendLine(string.Format(ci,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8:0.##},{9},{10:0.###},{11},"
                    + "{12:0.##},{13:0.##},{14:0.##},{15:0.###},{16},{17:0.##},{18}",
                    m.lod, m.srcFaces, m.faces, m.unplacedFaces, m.degenUvFaces,
                    m.invertedFaces, m.oobVerts, m.coveredTexels, m.utilizationPct,
                    m.overlapTexels, m.overlapPctOfCovered, m.overlapShellPairs,
                    m.tpuMean, m.tpuP1, m.tpuP99, m.tpuSpread,
                    m.xLodTexels, m.xLodContainedPct, m.misalignedGroups));
            try
            {
                File.WriteAllText(Path.Combine(outputDir, "stage_e_metrics.csv"),
                    csv.ToString());
            }
            catch (Exception ex)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage E3 metrics CSV write failed: {ex.Message}");
            }
        }

        /// <summary>Stage E3 diagnostic: per-LOD coverage/overlap raster —
        /// grey covered, red overlapping texels. lod{N}_overlap.png.</summary>
        static void WriteStageEOverlapPngs(string outputDir, Result r)
        {
            if (r.stageEOverlapPx == null || r.stageEMetricsRes <= 0) return;
            int res = r.stageEMetricsRes;
            for (int li = 0; li < r.stageEOverlapPx.Length; li++)
            {
                if (r.stageEOverlapPx[li] == null) continue;
                EncodePng(r.stageEOverlapPx[li], res, res,
                    Path.Combine(outputDir, $"lod{li}_overlap.png"));
            }
        }

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

        /// <summary>PR-3 Stage B: run the classical xatlas unwrap
        /// (sym-split + ARAP + pack, same params as proxy) on EACH
        /// non-deepest LOD independently. Stores per-LOD (uv2, tris)
        /// pairs on Result.fineClassicalUv2 / fineClassicalTris.
        /// Diagnostic-only at this stage — gives the operator a
        /// side-by-side view of how the fine LOD would unwrap if it
        /// were unwrapped without any inheritance from the proxy.
        /// Stage D's cascade projection will use these layouts as the
        /// seed for repacking unmatched shells.</summary>
        static void ComputeFineClassicalUnwraps(LODGroup lg, Options opts, Result r)
        {
            if (lg == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            int deepest = lodCount - 1;
            while (deepest > 0 && (lods[deepest].renderers == null
                || lods[deepest].renderers.Length == 0
                || lods[deepest].renderers[0] == null)) deepest--;

            r.fineClassicalUv2  = new Vector2[lodCount][];
            r.fineClassicalTris = new int[lodCount][];

            for (int li = 0; li < lodCount; li++)
            {
                if (li == deepest) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var src = mf != null ? mf.sharedMesh : null;
                if (src == null) continue;
                if (src.uv == null || src.uv.Length == 0) continue;

                var clone = UnityEngine.Object.Instantiate(src);
                clone.name = src.name + "_lod" + li + "_classical";
                try
                {
                    // Same pipeline as the proxy's clean variant:
                    // sym-split mirror-flipped shells, then pack via
                    // xatlas at the operator-configured resolution and
                    // padding. rotateCharts off so chart orientation
                    // stays comparable across LODs for the diagnostic.
                    var shells = UvShellExtractor.Extract(clone.uv, clone.triangles);
                    if (shells != null && shells.Count > 0)
                    {
                        int split = SymmetrySplitShells.Split(clone, shells);
                        if (split > 0)
                            UvtLog.Info(UvtLog.Category.Benchmark,
                                $"[HierRepack] lod{li} classical sym-split on '{lg.name}': {split} shells split");
                    }
                    var packOpts = RepackOptions.Default;
                    packOpts.resolution   = (uint)opts.atlasResolutionPx;
                    packOpts.padding      = (uint)opts.interDomainPaddingPx;
                    packOpts.rotateCharts = false;
                    var res = XatlasRepack.RepackSingle(clone, packOpts);
                    if (res.ok && clone.uv2 != null && clone.uv2.Length > 0)
                    {
                        r.fineClassicalUv2[li]  = clone.uv2;
                        r.fineClassicalTris[li] = clone.triangles;
                    }
                }
                catch (Exception ex)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] lod{li} classical unwrap failed on '{lg.name}': {ex.Message}");
                }
                finally { UnityEngine.Object.DestroyImmediate(clone); }
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

            // Pre-pass: compute each tri's UV bbox so the per-tri rogue
            // filter below can read it without recomputing. Adaptive
            // (median-relative) filter was removed -- it penalised
            // assets with heterogeneous chart sizes (many thin detail
            // charts + few large panel charts), where the median falls
            // to the thin-chart bbox and the large panel charts get
            // filtered as 'rogue'. Only the absolute cap remains.
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

                // Skip rogue tris from xatlas chart-seam splits (one
                // corner pinned at a different chart's UV). Only the
                // absolute cap is used — the previous adaptive (8x
                // median) cap punished assets with heterogeneous chart
                // sizes: a model with many thin detail charts + a few
                // large panel charts has a low median, the adaptive
                // threshold falls below the legit large charts' bbox,
                // and the BIG panels lose all their Poisson samples
                // (visible on Wooden_Box_Long where the four large
                // square panels had zero pink dots).
                const float kUvSeamBboxAbs = 0.35f;
                if (triUvBboxMax[f] > kUvSeamBboxAbs) continue;

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

        // ─── Stage C: per-LOD 3D shell extract + group seed ──────────

        /// <summary>For each LOD in the LODGroup, run BuildFaceData +
        /// ExtractShells with the same parameters (so shells across
        /// LODs come out comparable). Fills <c>r.perLodShells</c> and
        /// <c>r.perLodFaceToShell</c>. Then seeds <c>r.groups</c>: each
        /// shell on the deepest LOD becomes its own LightingDomainGroup
        /// with canonicalLod = deepest. Finer LODs' shells are left
        /// unassigned (perLodShellToGroup[li] = all -1) — Stage D's
        /// cascade matcher fills them in.</summary>
        static void ExtractPerLodShellsAndSeedGroups(LODGroup lg, Options opts,
            float meshDiag, Result r)
        {
            if (lg == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            r.perLodShells = new Shell3D[lodCount][];
            r.perLodFaceToShell = new int[lodCount][];
            r.perLodShellToGroup = new int[lodCount][];

            // Pick deepest LOD (last that has a renderer + mesh).
            int deepest = lodCount - 1;
            while (deepest > 0)
            {
                var rs = lods[deepest].renderers;
                if (rs != null && rs.Length > 0 && rs[0] != null
                    && rs[0].GetComponent<MeshFilter>()?.sharedMesh != null) break;
                deepest--;
            }

            for (int li = 0; li < lodCount; li++)
            {
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                var xform = rs[0].transform;

                int faceCount = mesh.triangles.Length / 3;
                if (faceCount == 0) continue;

                var faces = BuildFaceData(mesh, xform, meshDiag,
                    out Vector3[] worldVerts, out int[] rawTris,
                    out int[] canonicalTris, out _);
                var faceToShell = new int[faces.Length];
                var shells = ExtractShells(faces, worldVerts, rawTris,
                    canonicalTris, opts.shellNormalThresholdDeg,
                    opts.shellMergeAngleDeg, faceToShell, null);

                r.perLodShells[li] = shells;
                r.perLodFaceToShell[li] = faceToShell;
                r.perLodShellToGroup[li] = new int[shells.Length];
                for (int s = 0; s < shells.Length; s++)
                    r.perLodShellToGroup[li][s] = -1;

                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage C: LOD{li} '{lg.name}' → {shells.Length} shells");
            }

            // Seed groups from deepest LOD's shells.
            var deepShells = r.perLodShells[deepest];
            if (deepShells == null || deepShells.Length == 0)
            {
                r.groups = new LightingDomainGroup[0];
                return;
            }
            var groups = new LightingDomainGroup[deepShells.Length];
            var deepShellToGroup = r.perLodShellToGroup[deepest];
            for (int si = 0; si < deepShells.Length; si++)
            {
                groups[si] = new LightingDomainGroup
                {
                    groupId          = si,
                    canonicalLod     = deepest,
                    canonicalShellId = si,
                    members          = new List<(int, int)> { (deepest, si) },
                };
                deepShellToGroup[si] = si;
            }
            r.groups = groups;

            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[HierRepack] Stage C: '{lg.name}' seeded {groups.Length} groups "
                + $"(canonical=LOD{deepest}); finer LOD shells await Stage D matching");
        }

        // ─── Stage D: cascade grouping deep → fine ─────────────────────

        /// <summary>
        /// Stage D — cascade grouping deepest → LOD0. Walks each LOD
        /// transition (li_proxy, li_proxy-1) deep-side-first, Poisson-
        /// samples the deeper LOD's surface tagged with the deeper LOD's
        /// SHELL id, projects the samples onto the finer LOD's geometry
        /// (closest-point-on-face), and uses the per-shell vote tally to
        /// decide whether each finer shell joins the parent's lighting
        /// domain group or starts its own.
        ///
        /// Membership only — NO UV is written here. Stage E will pick
        /// each domain's canonical chart and pack the final atlas; this
        /// stage only fills <c>r.perLodShellToGroup</c> and grows
        /// <c>r.groups</c>.
        ///
        /// Match rule: per finer shell, look up
        /// <c>perLodFaceToShell[li_proxy-1][f]</c> for every face that
        /// received hits, weighted by hit count → modal proxy shell +
        /// its hit fraction. If the modal fraction ≥ <c>kMatchFrac</c>
        /// AND there are at least <c>kMinHits</c> total hits, join the
        /// parent's group; otherwise create a new group whose canonical
        /// member is the finer shell itself (cluster of detail geometry
        /// missing from the deeper LOD — windows, screws, trim).
        /// </summary>
        static void CascadeGroupShells(LODGroup lg, Options opts,
            float meshDiag, Result r)
        {
            if (lg == null || r == null) return;
            if (r.perLodShells == null || r.perLodFaceToShell == null
                || r.perLodShellToGroup == null || r.groups == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;

            // Same deepest-LOD picker as Stage C — must agree so the
            // seed groups Stage C wrote line up with our cascade start.
            int deepest = lodCount - 1;
            while (deepest > 0)
            {
                var rs = lods[deepest].renderers;
                if (rs != null && rs.Length > 0 && rs[0] != null
                    && rs[0].GetComponent<MeshFilter>()?.sharedMesh != null) break;
                deepest--;
            }
            if (deepest <= 0) return;

            // Stage E will read r.groups as an array — but cascade may add
            // unmatched groups for finer LODs as we descend, so work on a
            // growing list and snapshot back to array at the end.
            var groups = new List<LightingDomainGroup>(r.groups);
            r.cascadeStats = new List<CascadeStat>();

            // Tuning knobs — now in Options so the Stage-D threshold sweep
            // can vary them per cell. Defaults (0.5 / 4) match plan v2
            // §"Дыра 6 matched threshold". A non-positive minHits would let
            // zero-hit shells join on a 0/0 vote; clamp to 1.
            float kMatchFrac = Mathf.Clamp01(opts.cascadeMatchFrac);
            int   kMinHits   = Mathf.Max(1, opts.cascadeMinHits);
            float distAbsThreshold = opts.overlayDistNorm * meshDiag;

            for (int liProxy = deepest; liProxy >= 1; liProxy--)
            {
                int liFine = liProxy - 1;

                var proxyShells     = r.perLodShells[liProxy];
                var fineShells      = r.perLodShells[liFine];
                var proxyFaceToShell = r.perLodFaceToShell[liProxy];
                var fineFaceToShell  = r.perLodFaceToShell[liFine];
                if (proxyShells == null || fineShells == null
                    || proxyFaceToShell == null || fineFaceToShell == null) continue;

                var rsProxy = lods[liProxy].renderers;
                var rsFine  = lods[liFine].renderers;
                if (rsProxy == null || rsProxy.Length == 0 || rsProxy[0] == null) continue;
                if (rsFine  == null || rsFine .Length == 0 || rsFine [0] == null) continue;
                var mfProxy = rsProxy[0].GetComponent<MeshFilter>();
                var mfFine  = rsFine [0].GetComponent<MeshFilter>();
                var proxyMesh = mfProxy != null ? mfProxy.sharedMesh : null;
                var fineMesh  = mfFine  != null ? mfFine .sharedMesh : null;
                if (proxyMesh == null || fineMesh == null) continue;

                // 1) Sample the deeper LOD's surface, tagged with deeper-LOD
                //    SHELL id (not UV-derived). Uses the same density knob
                //    as the existing GenerateProxySamples — that one drives
                //    UV-aware projection on the deepest LOD only, this one
                //    drives shell-membership voting between adjacent LODs.
                var proxyFaces = BuildFaceData(proxyMesh, rsProxy[0].transform,
                    meshDiag, out Vector3[] proxyWorld, out int[] proxyRawTris,
                    out _, out _);
                int proxyFaceCount = proxyFaces.Length;
                if (proxyFaceCount == 0) continue;

                float diagSq = Mathf.Max(meshDiag * meshDiag, 1e-6f);
                float densityPerSqMeshDiag = Mathf.Max(opts.proxySampleDensity, 1);
                var samples = new List<(Vector3 pos, int shellId)>(proxyFaceCount * 6);
                uint rng = 0x9E3779B9u;

                for (int f = 0; f < proxyFaceCount; f++)
                {
                    if (proxyFaces[f].area <= 0f) continue;
                    int shellId = (f < proxyFaceToShell.Length) ? proxyFaceToShell[f] : -1;
                    if (shellId < 0) continue;

                    Vector3 A = proxyWorld[proxyRawTris[f * 3]];
                    Vector3 B = proxyWorld[proxyRawTris[f * 3 + 1]];
                    Vector3 C = proxyWorld[proxyRawTris[f * 3 + 2]];

                    int n = Mathf.Max(3, Mathf.CeilToInt(
                        densityPerSqMeshDiag * proxyFaces[f].area / diagSq));
                    for (int s = 0; s < n; s++)
                    {
                        rng = unchecked(rng * 1664525u + 1013904223u);
                        float u = (rng & 0xFFFFFFu) / 16777216f;
                        rng = unchecked(rng * 1664525u + 1013904223u);
                        float v = (rng & 0xFFFFFFu) / 16777216f;
                        if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                        float w = 1f - u - v;
                        samples.Add((A * w + B * u + C * v, shellId));
                    }
                }
                if (samples.Count == 0) continue;

                // 2) Project samples onto the finer LOD's geometry. For each
                //    fine face, tally per-deeper-shell hit count so we can
                //    later aggregate per fine SHELL.
                var fineFaces = BuildFaceData(fineMesh, rsFine[0].transform,
                    meshDiag, out Vector3[] fineWorld, out int[] fineRawTris,
                    out _, out _);
                int fineFaceCount = fineFaces.Length;
                if (fineFaceCount == 0) continue;
                BuildDeepAabbs(fineWorld, fineRawTris,
                    out var fineMin, out var fineMax);

                // perFineFace[shellId → hits]. List of dicts keeps total
                // memory proportional to "faces that got any hits".
                var perFineFace = new Dictionary<int, int>[fineFaceCount];

                int totalHits = 0, totalMissed = 0;
                foreach (var sm in samples)
                {
                    int closest = ProjectVertexToDeepMesh(sm.pos,
                        fineFaces, fineWorld, fineRawTris,
                        fineMin, fineMax, out float dist);
                    if (closest < 0 || dist > distAbsThreshold)
                    {
                        totalMissed++;
                        continue;
                    }
                    var bucket = perFineFace[closest];
                    if (bucket == null)
                    {
                        bucket = new Dictionary<int, int>(2);
                        perFineFace[closest] = bucket;
                    }
                    bucket.TryGetValue(sm.shellId, out int cnt);
                    bucket[sm.shellId] = cnt + 1;
                    totalHits++;
                }

                // 3) Aggregate per finer SHELL. perFineShellTally[fineShellId]
                //    [proxyShellId] = total hits across the fine shell's faces.
                var perFineShellTally = new Dictionary<int, int>[fineShells.Length];
                for (int s = 0; s < fineShells.Length; s++)
                    perFineShellTally[s] = new Dictionary<int, int>(2);
                for (int f = 0; f < fineFaceCount; f++)
                {
                    var bucket = perFineFace[f];
                    if (bucket == null) continue;
                    int fineShell = (f < fineFaceToShell.Length) ? fineFaceToShell[f] : -1;
                    if (fineShell < 0 || fineShell >= fineShells.Length) continue;
                    var dst = perFineShellTally[fineShell];
                    foreach (var kv in bucket)
                    {
                        dst.TryGetValue(kv.Key, out int c);
                        dst[kv.Key] = c + kv.Value;
                    }
                }

                // 4) For each finer shell, decide: join parent group or new.
                // Tiny-shell rule (sweep insight 2026-06-03): finer shells
                // below skipAreaFrac × totalFineArea AND ≤ skipMaxFaceCount
                // get force-joined to their modal proxy parent even when
                // matchedFrac / minHits would normally fail them — they're
                // the thin trim / wire / fastener that the sweep showed was
                // the dominant source of fresh-group explosion (Carousel
                // 269→922) without affecting major lighting domains. If a
                // tiny shell has NO proxy match at all (bestProxy<0; deeper
                // LOD genuinely lacks it), it still opens a fresh group —
                // a topological-neighbour fallback would handle that case
                // but adds an adjacency build; defer until the data shows
                // it's needed. Counted separately in the log.
                float totalFineArea = 0f;
                for (int s = 0; s < fineShells.Length; s++)
                    totalFineArea += fineShells[s].totalArea;
                float tinyAreaAbs = Mathf.Max(0f, opts.skipAreaFrac) * totalFineArea;
                int tinyFaceMax = Mathf.Max(0, opts.skipMaxFaceCount);

                int joined = 0, fresh = 0, tinyJoined = 0, tinyOrphan = 0;
                int alreadyAssigned = 0;
                var proxyShellToGroup = r.perLodShellToGroup[liProxy];
                var fineShellToGroup  = r.perLodShellToGroup[liFine];

                for (int s = 0; s < fineShells.Length; s++)
                {
                    if (fineShellToGroup[s] >= 0) { alreadyAssigned++; continue; }

                    var tally = perFineShellTally[s];
                    int shellTotal = 0, bestProxy = -1, bestCount = 0;
                    foreach (var kv in tally)
                    {
                        shellTotal += kv.Value;
                        if (kv.Value > bestCount) { bestCount = kv.Value; bestProxy = kv.Key; }
                    }
                    float matchedFrac = shellTotal > 0 ? (float)bestCount / shellTotal : 0f;

                    bool parentResolves = bestProxy >= 0
                                       && bestProxy < proxyShellToGroup.Length
                                       && proxyShellToGroup[bestProxy] >= 0;
                    bool normalJoin = parentResolves
                                   && shellTotal >= kMinHits
                                   && matchedFrac >= kMatchFrac;
                    bool isTiny = fineShells[s].totalArea <= tinyAreaAbs
                               && fineShells[s].faceCount <= tinyFaceMax;
                    bool tinyForceJoin = isTiny && parentResolves && !normalJoin;

                    if (normalJoin || tinyForceJoin)
                    {
                        int gid = proxyShellToGroup[bestProxy];
                        fineShellToGroup[s] = gid;
                        var grp = groups[gid];
                        if (grp.members == null) grp.members = new List<(int, int)>();
                        grp.members.Add((liFine, s));
                        groups[gid] = grp;
                        if (tinyForceJoin) tinyJoined++; else joined++;
                    }
                    else
                    {
                        int gid = groups.Count;
                        groups.Add(new LightingDomainGroup
                        {
                            groupId          = gid,
                            canonicalLod     = liFine,
                            canonicalShellId = s,
                            members          = new List<(int, int)> { (liFine, s) },
                        });
                        fineShellToGroup[s] = gid;
                        fresh++;
                        if (isTiny) tinyOrphan++;
                    }
                }

                r.cascadeStats.Add(new CascadeStat
                {
                    liProxy = liProxy,
                    liFine  = liFine,
                    samples = samples.Count,
                    hits    = totalHits,
                    missed  = totalMissed,
                    joined  = joined,
                    fresh   = fresh,
                    reused  = alreadyAssigned,
                    tinyJoined = tinyJoined,
                    tinyOrphan = tinyOrphan,
                });

                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage D: '{lg.name}' LOD{liProxy}→LOD{liFine} "
                    + $"samples={samples.Count} hits={totalHits} missed={totalMissed} "
                    + $"| joined={joined} tinyJoined={tinyJoined} new={fresh} "
                    + $"(tinyOrphan={tinyOrphan}) reused={alreadyAssigned} "
                    + $"(thresholds mf>={kMatchFrac:F2} mh>={kMinHits} "
                    + $"tinyArea<={opts.skipAreaFrac:F3}×total tinyFaces<={tinyFaceMax})");
            }

            r.groups = groups.ToArray();
            UvtLog.Info(UvtLog.Category.Benchmark,
                $"[HierRepack] Stage D: '{lg.name}' final group count = {r.groups.Length} "
                + $"(seeded={r.perLodShells[deepest]?.Length ?? 0} from deepest LOD{deepest})");
        }

        // ─── Stage E (slice E1): pack lighting-domain canonical charts ──

        /// <summary>Stage E, slice 1 — define the SHARED atlas layout. For
        /// each lighting-domain group, take its canonical (deepest-member)
        /// shell — the one that found no deeper match, i.e. the seed we
        /// REPACK — project its faces onto the shell's own plane in WORLD
        /// UNITS (inU = dot(worldPos − centroid, basisU); inV likewise; NO
        /// per-shell normalisation, so proportions are real) and feed it to
        /// xatlas as a UV mesh with faceMaterial = groupId. Pack runs at a
        /// FIXED texelsPerUnit (sized so all canonical area fits the atlas at
        /// ~packEff), so every chart gets identical texel density. Each
        /// material is a hard chart boundary → one chart per group. From each
        /// group's placed verts we least-squares-fit the affine
        /// inU→atlasU / inV→atlasV (<c>r.domainPlacements[groupId]</c>); the
        /// canonical reproduces its own placement and <see cref="BuildCascadedUv2"/>
        /// reuses the affine for every finer member. (<c>r.domainAtlasRects</c>
        /// keeps the bbox for the diagnostic.)
        ///
        /// This is layout only. The cascade payoff — every LOD's matched
        /// shells PROJECTING into their group's rect so one bake is valid
        /// across all LODs, and unmatched shells owning their repacked rect —
        /// is <see cref="BuildCascadedUv2"/>. xatlas output UV is already
        /// normalised [0,1] in this native build (confirmed against
        /// proxy_uv2_auto.png). <c>r.domainAtlasUv</c> / <c>r.domainAtlasTris</c>
        /// back the domains_atlas.png diagnostic.</summary>
        static void PackDomainCharts(LODGroup lg, Options opts, float meshDiag, Result r)
        {
            if (r.groups == null || r.groups.Length == 0) return;
            if (r.perLodShells == null) return;

            var lods = lg.GetLODs();
            int lodCount = lods.Length;

            // Lazy per-LOD geometry cache (worldVerts + rawTris). Stage C
            // didn't keep these on the Result; recompute on first use. Most
            // groups' canonical shells live on the deepest LOD, but fresh /
            // tinyOrphan groups born on a finer LOD have their canonical there.
            var worldVertsByLod = new Vector3[lodCount][];
            var rawTrisByLod = new int[lodCount][];
            var uv0ByLod = new Vector2[lodCount][];   // authored UV0, indexed like worldVerts
            var builtLod = new bool[lodCount];
            void EnsureLodGeometry(int li)
            {
                if (builtLod[li]) return;
                builtLod[li] = true;
                if (li < 0 || li >= lodCount) return;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) return;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) return;
                BuildFaceData(mesh, rs[0].transform, meshDiag,
                    out Vector3[] wv, out int[] rt, out _, out _);
                worldVertsByLod[li] = wv;
                rawTrisByLod[li] = rt;
                uv0ByLod[li] = mesh.uv;   // Vector2[] sized to vertexCount, or empty
            }

            // Build one UV mesh per canonical face. We PRESERVE the shell's own
            // authored UV0 — its real unwrap — and only apply a single uniform
            // scale + recentre per shell. No planar re-projection (that threw
            // the shell's parameterisation away and folded curves). The scale
            // S = sqrt(area3D / areaUV0) makes every canonical's UV-area equal
            // its 3D area, so a fixed texelsPerUnit at pack time → identical
            // texel density everywhere, with the shell shape kept intact.
            // faceMaterial = groupId. We record each input vert's group + its
            // scaled coord so the readback can fit the per-group affine, and we
            // stash (uvc, S) per group so BuildCascadedUv2 can transform finer
            // members' UV0 into the same frame.
            var uvList = new List<float>(1024);
            var idxList = new List<uint>(1024);
            var faceMatList = new List<uint>(512);
            var inputVertGroup = new List<int>(1024);  // myVertIdx → groupId
            var inputVertU = new List<float>(1024);    // myVertIdx → scaled uv0.x
            var inputVertV = new List<float>(1024);    // myVertIdx → scaled uv0.y
            var pendingUvc = new Vector2[r.groups.Length];
            var pendingScale = new float[r.groups.Length];
            uint vCounter = 0;
            double totalCanonArea = 0.0;

            for (int g = 0; g < r.groups.Length; g++)
            {
                var grp = r.groups[g];
                int cl = grp.canonicalLod;
                if (cl < 0 || cl >= lodCount) continue;
                var shellsAtCl = r.perLodShells[cl];
                if (shellsAtCl == null || grp.canonicalShellId < 0
                    || grp.canonicalShellId >= shellsAtCl.Length) continue;
                EnsureLodGeometry(cl);
                var rt = rawTrisByLod[cl];
                var uv0 = uv0ByLod[cl];
                if (rt == null || uv0 == null || uv0.Length == 0) continue;

                var sh = shellsAtCl[grp.canonicalShellId];
                if (sh.faceIndices == null || sh.faceIndices.Count == 0) continue;

                // Preserve the shell's UV0 island: centroid + UV0 area, then a
                // single uniform scale to normalise texel density (UV-area →
                // 3D-area). Shape untouched.
                Vector2 uvc = Vector2.zero; int cornerCount = 0; double uvArea = 0.0;
                foreach (int f in sh.faceIndices)
                {
                    if (f < 0 || f * 3 + 2 >= rt.Length) continue;
                    int a = rt[f * 3], b = rt[f * 3 + 1], c = rt[f * 3 + 2];
                    if (a >= uv0.Length || b >= uv0.Length || c >= uv0.Length) continue;
                    uvc += uv0[a] + uv0[b] + uv0[c]; cornerCount += 3;
                    uvArea += 0.5 * Mathf.Abs((uv0[b].x - uv0[a].x) * (uv0[c].y - uv0[a].y)
                                            - (uv0[c].x - uv0[a].x) * (uv0[b].y - uv0[a].y));
                }
                if (cornerCount == 0) continue;
                uvc /= cornerCount;
                float S = (uvArea > 1e-12)
                    ? Mathf.Sqrt(sh.totalArea / (float)uvArea) : 1f;
                pendingUvc[grp.groupId] = uvc;
                pendingScale[grp.groupId] = S;
                totalCanonArea += sh.totalArea;

                foreach (int f in sh.faceIndices)
                {
                    if (f < 0 || f * 3 + 2 >= rt.Length) continue;
                    int e0 = rt[f * 3], e1 = rt[f * 3 + 1], e2 = rt[f * 3 + 2];
                    if (e0 >= uv0.Length || e1 >= uv0.Length || e2 >= uv0.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = rt[f * 3 + k];
                        float u = (uv0[vi].x - uvc.x) * S;   // preserved UV0, scaled
                        float v = (uv0[vi].y - uvc.y) * S;
                        uvList.Add(u);
                        uvList.Add(v);
                        idxList.Add(vCounter);
                        inputVertGroup.Add(grp.groupId);
                        inputVertU.Add(u);
                        inputVertV.Add(v);
                        vCounter++;
                    }
                    faceMatList.Add((uint)grp.groupId);
                }
            }

            // Fixed texel density: with each shell's UV scaled so UV-area ==
            // 3D-area, the input UV is effectively in world units, so sizing the
            // pack by total canonical 3D area gives every chart the SAME
            // texels/unit.
            const float kPackEff = 0.5f;
            float texelsPerUnit = (totalCanonArea > 1e-9)
                ? opts.atlasResolutionPx * Mathf.Sqrt(kPackEff / (float)totalCanonArea)
                : 0f;

            int vc = (int)vCounter;
            int ic = idxList.Count;
            int fc = faceMatList.Count;
            if (vc == 0 || ic == 0 || fc == 0)
            {
                UvtLog.Warn(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage E: '{lg.name}' no canonical chart geometry — skipped");
                return;
            }

            var uvArr = uvList.ToArray();
            var idxArr = idxList.ToArray();
            var faceMatArr = faceMatList.ToArray();

            XatlasNative.xatlasCreate();
            try
            {
                int addErr = XatlasNative.xatlasAddUvMesh(uvArr, (uint)vc, idxArr,
                    (uint)ic, faceMatArr, (uint)fc);
                if (addErr != 0)
                {
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] Stage E: '{lg.name}' xatlasAddUvMesh err={addErr}");
                    return;
                }
                XatlasNative.xatlasComputeCharts();
                XatlasNative.xatlasPackCharts(
                    maxChartSize: 0,
                    padding: (uint)opts.interDomainPaddingPx,
                    texelsPerUnit: texelsPerUnit,   // fixed → identical texel density
                    resolution: (uint)opts.atlasResolutionPx,
                    bilinear: 1,
                    blockAlign: 0,
                    bruteForce: 1,
                    rotateCharts: 0,
                    rotateChartsToAxis: 0);

                if (XatlasNative.xatlasGetMeshCount() <= 0) return;
                int outVc = XatlasNative.xatlasGetOutputVertexCount(0);
                int outIc = XatlasNative.xatlasGetOutputIndexCount(0);
                if (outVc <= 0 || outIc <= 0) return;

                var xref = new uint[outVc];
                var outUvFlat = new float[outVc * 2];
                var chartIdx = new uint[outVc];
                XatlasNative.xatlasGetOutputVertexData(0, xref, outUvFlat, chartIdx, outVc);
                var outIndsU = new uint[outIc];
                XatlasNative.xatlasGetOutputIndices(0, outIndsU, outIc);

                // Read placed UV ([0,1]) and, per group, fit the affine
                // inU→atlasU and inV→atlasV by least squares over the canonical
                // chart's output verts (exact: xatlas applied a uniform scale +
                // translation, rotateCharts:0 so no rotation; LSQ is robust to
                // any axis flip). xref[i] maps an output vert back to our input
                // vert, whose group + (inU,inV) we recorded. Also keep the bbox
                // rect for the diagnostic.
                int nG = r.groups.Length;
                var domUv = new Vector2[outVc];
                var rectMin = new Vector2[nG];
                var rectMax = new Vector2[nG];
                // LSQ accumulators per group, per axis: n, Σin, Σout, Σin², Σin·out
                var aN  = new double[nG];
                var aSi = new double[nG]; var aSoU = new double[nG];
                var aSii = new double[nG]; var aSioU = new double[nG];
                var aSiV = new double[nG]; var aSoV = new double[nG];
                var aSiiV = new double[nG]; var aSioV = new double[nG];
                for (int g = 0; g < nG; g++)
                {
                    rectMin[g] = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                    rectMax[g] = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                }
                for (int i = 0; i < outVc; i++)
                {
                    var uv = new Vector2(outUvFlat[i * 2], outUvFlat[i * 2 + 1]);
                    domUv[i] = uv;
                    int myVert = (int)xref[i];
                    if (myVert < 0 || myVert >= inputVertGroup.Count) continue;
                    int gid = inputVertGroup[myVert];
                    if (gid < 0 || gid >= nG) continue;
                    double inU = inputVertU[myVert], inV = inputVertV[myVert];
                    aN[gid]   += 1.0;
                    aSi[gid]  += inU;  aSoU[gid] += uv.x;
                    aSii[gid] += inU * inU; aSioU[gid] += inU * uv.x;
                    aSiV[gid] += inV;  aSoV[gid] += uv.y;
                    aSiiV[gid]+= inV * inV; aSioV[gid] += inV * uv.y;
                    if (uv.x < rectMin[gid].x) rectMin[gid].x = uv.x;
                    if (uv.y < rectMin[gid].y) rectMin[gid].y = uv.y;
                    if (uv.x > rectMax[gid].x) rectMax[gid].x = uv.x;
                    if (uv.y > rectMax[gid].y) rectMax[gid].y = uv.y;
                }

                var rects = new Rect[nG];
                var placements = new DomainPlacement[nG];
                int packedGroups = 0;
                for (int g = 0; g < nG; g++)
                {
                    if (rectMax[g].x < rectMin[g].x)
                    {
                        rects[g] = new Rect(0f, 0f, 0f, 0f); // group contributed nothing
                        continue;
                    }
                    rects[g] = new Rect(rectMin[g].x, rectMin[g].y,
                        rectMax[g].x - rectMin[g].x, rectMax[g].y - rectMin[g].y);

                    double n = aN[g];
                    double denU = n * aSii[g] - aSi[g] * aSi[g];
                    double denV = n * aSiiV[g] - aSiV[g] * aSiV[g];
                    bool okU = System.Math.Abs(denU) > 1e-12;
                    bool okV = System.Math.Abs(denV) > 1e-12;
                    if (n >= 2.0 && (okU || okV))
                    {
                        double suD = okU ? (n * aSioU[g] - aSi[g] * aSoU[g]) / denU : 0.0;
                        double svD = okV ? (n * aSioV[g] - aSiV[g] * aSoV[g]) / denV : 0.0;
                        // A zero-variance axis (perfectly straight axis-aligned
                        // strip in UV0) leaves that axis's LSQ slope
                        // unconstrained — NOT a reason to drop the whole group:
                        // every member shell on every LOD would silently lose
                        // its placement. xatlas applied a uniform scale
                        // (rotateCharts:0, no flip), so borrow the resolved
                        // axis's magnitude; finer members DO vary along the
                        // degenerate axis and land at the right density.
                        if (!okU) suD = System.Math.Abs(svD);
                        if (!okV) svD = System.Math.Abs(suD);
                        placements[g] = new DomainPlacement
                        {
                            uvc = pendingUvc[g],
                            scale = pendingScale[g],
                            su = (float)suD,
                            ou = (float)((aSoU[g] - suD * aSi[g]) / n),
                            sv = (float)svD,
                            ov = (float)((aSoV[g] - svD * aSiV[g]) / n),
                            valid = true,
                        };
                    }
                    else if (n >= 1.0 && texelsPerUnit > 0f)
                    {
                        // Both axes degenerate — the canonical chart collapsed
                        // to a point in UV0. Fall back to the designed mapping:
                        // the pack ran at a fixed texelsPerUnit, so the scale is
                        // texelsPerUnit/resolution; anchor at the mean placed UV
                        // so members at least land inside their packed slot.
                        double s = texelsPerUnit / (double)opts.atlasResolutionPx;
                        placements[g] = new DomainPlacement
                        {
                            uvc = pendingUvc[g],
                            scale = pendingScale[g],
                            su = (float)s,
                            ou = (float)((aSoU[g] - s * aSi[g]) / n),
                            sv = (float)s,
                            ov = (float)((aSoV[g] - s * aSiV[g]) / n),
                            valid = true,
                        };
                    }
                    packedGroups++;
                }

                var domTris = new int[outIc];
                for (int i = 0; i < outIc; i++) domTris[i] = (int)outIndsU[i];

                r.domainAtlasUv = domUv;
                r.domainAtlasTris = domTris;
                r.domainAtlasRects = rects;
                r.domainPlacements = placements;

                uint aw = XatlasNative.xatlasGetAtlasWidth();
                uint ah = XatlasNative.xatlasGetAtlasHeight();
                uint charts = XatlasNative.xatlasGetChartCount();
                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage E: '{lg.name}' packed {packedGroups}/{r.groups.Length} "
                    + $"domain charts (xatlas charts={charts}) into {aw}×{ah} atlas "
                    + $"(canonical faces={fc}, outVerts={outVc}, "
                    + $"texels/unit={texelsPerUnit:F1}, canonArea={totalCanonArea:F2})");
            }
            finally { XatlasNative.xatlasDestroy(); }
        }

        // ─── Stage E (slice E2): cascade — align every LOD to its domain ──

        /// <summary>Stage E, slice 2 — the cascade payoff. Stage E1 PRESERVED
        /// each group's canonical shell UV0 (recentre + uniform scale) and
        /// fitted <c>r.domainPlacements[groupId]</c>: uvc, scale, and the affine
        /// (scaled-UV0 → atlas UV). Here EVERY shell on EVERY LOD reuses that
        /// SAME placement on its OWN authored UV0:
        /// <c>in = (uv0 − uvc)·scale; uv = (su·in.x+ou, sv·in.y+ov)</c>. Members
        /// of a lighting domain share UV0 layout across LODs, so the same UV0
        /// coord maps to the same atlas texel on every LOD → a matched finer
        /// shell lands in the SAME atlas region as its canonical, at the SAME
        /// texel density, with the shell's real unwrap intact (no planar
        /// re-projection, no distortion). An unmatched shell is the canonical of
        /// its own fresh group and uses the placement for its repacked slot.
        ///
        /// Fills <c>r.finalUv2</c> / <c>r.finalTris</c> /
        /// <c>r.finalSourceVertexIdx</c> per LOD (output verts are one per face
        /// corner — no dedup; Stage F copies attributes from the source vertex
        /// each one points at). Rendered by lod{N}_final_uv2.png.</summary>
        static void BuildCascadedUv2(LODGroup lg, Options opts, float meshDiag, Result r)
        {
            if (r.groups == null || r.groups.Length == 0) return;
            if (r.perLodShells == null || r.perLodShellToGroup == null) return;
            if (r.domainPlacements == null) return;

            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            r.finalUv2 = new Vector2[lodCount][];
            r.finalTris = new int[lodCount][];
            r.finalSourceVertexIdx = new int[lodCount][];
            r.finalFaceShell = new int[lodCount][];
            r.finalFaceGroup = new int[lodCount][];
            r.finalUnplacedFaces = new int[lodCount];

            for (int li = 0; li < lodCount; li++)
            {
                var shells = r.perLodShells[li];
                var shellToGroup = r.perLodShellToGroup[li];
                if (shells == null || shellToGroup == null) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                BuildFaceData(mesh, rs[0].transform, meshDiag,
                    out Vector3[] _, out int[] rt, out _, out _);
                var uv0 = mesh.uv;
                if (uv0 == null || uv0.Length == 0) continue;

                var uvOut  = new List<Vector2>(rt.Length);
                var triOut = new List<int>(rt.Length);
                var srcOut = new List<int>(rt.Length);
                var shellOut = new List<int>(rt.Length / 3);  // per emitted face
                var groupOut = new List<int>(rt.Length / 3);
                int unplacedShells = 0, unplacedFaces = 0;

                for (int s = 0; s < shells.Length; s++)
                {
                    int gid = (s < shellToGroup.Length) ? shellToGroup[s] : -1;
                    bool placed = gid >= 0 && gid < r.groups.Length
                        && gid < r.domainPlacements.Length
                        && r.domainPlacements[gid].valid;
                    // A shell whose group never got a placement (Stage E1
                    // produced nothing for it) must STILL be emitted —
                    // skipping it would delete real geometry from the final
                    // meshes. Park its uv2 at (0,0): a bad bake on those
                    // faces, never a hole in the mesh.
                    var pl = placed ? r.domainPlacements[gid] : default;
                    if (!placed) unplacedShells++;

                    // PRESERVE the shell: take its own authored UV0, recentre +
                    // uniform-scale into the SAME frame the canonical was fitted
                    // in (canonical's uvc + scale), then apply the group affine.
                    // Members of a domain share UV0 layout across LODs, so the
                    // same UV0 coord maps to the same atlas texel on every LOD —
                    // one bake valid everywhere, at uniform texel density, with
                    // the shell's real unwrap intact (no planar re-projection).
                    var sh = shells[s];
                    if (sh.faceIndices == null) continue;
                    foreach (int f in sh.faceIndices)
                    {
                        if (f < 0 || f * 3 + 2 >= rt.Length) continue;
                        int a = rt[f * 3], b = rt[f * 3 + 1], c = rt[f * 3 + 2];
                        if (a >= uv0.Length || b >= uv0.Length || c >= uv0.Length) continue;
                        if (!placed) unplacedFaces++;
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = rt[f * 3 + k];
                            Vector2 uvv = Vector2.zero;
                            if (placed)
                            {
                                float inU = (uv0[vi].x - pl.uvc.x) * pl.scale;
                                float inV = (uv0[vi].y - pl.uvc.y) * pl.scale;
                                uvv = new Vector2(pl.su * inU + pl.ou,
                                                  pl.sv * inV + pl.ov);
                            }
                            triOut.Add(uvOut.Count);
                            uvOut.Add(uvv);
                            srcOut.Add(vi);
                        }
                        shellOut.Add(s);
                        groupOut.Add(gid);
                    }
                }

                r.finalUv2[li] = uvOut.ToArray();
                r.finalTris[li] = triOut.ToArray();
                r.finalSourceVertexIdx[li] = srcOut.ToArray();
                r.finalFaceShell[li] = shellOut.ToArray();
                r.finalFaceGroup[li] = groupOut.ToArray();
                r.finalUnplacedFaces[li] = unplacedFaces;

                if (unplacedFaces > 0)
                    UvtLog.Warn(UvtLog.Category.Benchmark,
                        $"[HierRepack] Stage E2: '{lg.name}' LOD{li} — {unplacedShells} shells "
                        + $"({unplacedFaces} faces) had no valid domain placement; emitted at "
                        + "uv2 (0,0) so the final mesh keeps its geometry. Check Stage E1 warnings.");
                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage E2: '{lg.name}' LOD{li} cascaded uv2 "
                    + $"verts={uvOut.Count} tris={triOut.Count / 3} shells={shells.Length}");
            }
        }

        // ─── Stage E (slice E3): objective atlas quality metrics ─────

        /// <summary>True when two emitted faces share a source vertex or a
        /// near-coincident atlas-UV vertex — i.e. they're seam/fan-adjacent in
        /// the same island, so a tied texel between them is rasterisation
        /// noise, not a UV overlap. Mirror-reused islands (the defect this
        /// filter must NOT hide) live on different shells and never reach this
        /// check. Known blind spot: a single triangle folded exactly over its
        /// edge-neighbour passes as adjacent; folds deeper than one triangle
        /// still get caught via their non-adjacent pairs.</summary>
        static bool FacesTouch(Vector2[] uv, int[] tris, int[] srcIdx,
            int fa, int fb, float weldTol2)
        {
            for (int i = 0; i < 3; i++)
            {
                int va = tris[fa * 3 + i];
                for (int j = 0; j < 3; j++)
                {
                    int vb = tris[fb * 3 + j];
                    if (srcIdx != null && srcIdx[va] == srcIdx[vb]) return true;
                    float du = uv[va].x - uv[vb].x;
                    float dv = uv[va].y - uv[vb].y;
                    if (du * du + dv * dv <= weldTol2) return true;
                }
            }
            return false;
        }

        /// <summary>Stage E, slice 3 — measure the final per-LOD UV2 on an
        /// atlas-resolution texel-centre raster so the pipeline has an
        /// OBJECTIVE defect scalar instead of eyeballed PNGs. Per LOD:
        ///   • overlapTexels / overlapShellPairs — texels claimed by 2+
        ///     triangles that are neither the same face nor same-shell
        ///     seam-adjacent. Cross-shell conflicts always count (UV0
        ///     mirror-reuse between shells IS the defect; islands that merely
        ///     touch contribute ~1px lines vs full-area genuine overlaps).
        ///   • invertedFaces / degenUvFaces / oobVerts — winding flips,
        ///     zero-area UV, verts outside [0,1].
        ///   • tpu* — area-weighted texels-per-world-unit spread; preserve-UV0
        ///     + fixed texelsPerUnit should keep this near 1×.
        ///   • xLodContainedPct / misalignedGroups — cross-LOD check: texels
        ///     of groups whose canonical lives on another LOD must land inside
        ///     that canonical's footprint (3×3 dilated). Low containment =
        ///     the asset violates the shared-UV0-layout assumption → the bake
        ///     would NOT be valid across LODs.
        /// Pure readback — no mesh/UV mutation. Also fills the
        /// lod{N}_overlap.png raster buffers.</summary>
        static void ComputeStageEMetrics(LODGroup lg, Options opts, Result r)
        {
            if (r.finalUv2 == null || r.finalTris == null) return;
            if (r.groups == null || r.finalFaceShell == null || r.finalFaceGroup == null) return;

            int res = Mathf.Clamp(opts.atlasResolutionPx, 64, 2048);
            var lods = lg.GetLODs();
            int lodCount = Mathf.Min(lods.Length, r.finalUv2.Length);
            r.stageEMetricsRes = res;
            r.stageEMetrics = new StageEMetrics[lodCount];
            r.stageEOverlapPx = new Color32[lodCount][];

            var ownerFaceByLod = new int[lodCount][];
            float weldTol = 0.75f / res;
            float weldTol2 = weldTol * weldTol;
            float halfTexel = 0.5f / res;

            for (int li = 0; li < lodCount; li++)
            {
                var m = new StageEMetrics { lod = li };
                r.stageEMetrics[li] = m;
                var uv = r.finalUv2[li];
                var tris = r.finalTris[li];
                var srcIdx = (r.finalSourceVertexIdx != null
                    && li < r.finalSourceVertexIdx.Length)
                    ? r.finalSourceVertexIdx[li] : null;
                var fShell = (li < r.finalFaceShell.Length) ? r.finalFaceShell[li] : null;
                if (uv == null || tris == null || fShell == null) continue;

                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                // World-space verts once per LOD — density needs 3D areas.
                var localVerts = mesh.vertices;
                var xform = rs[0].transform;
                var wv = new Vector3[localVerts.Length];
                for (int i = 0; i < localVerts.Length; i++)
                    wv[i] = xform.TransformPoint(localVerts[i]);

                int srcFaces = 0;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    srcFaces += (int)(mesh.GetIndexCount(sm) / 3);

                int faceCount = tris.Length / 3;
                m.srcFaces = srcFaces;
                m.faces = faceCount;
                m.unplacedFaces = (r.finalUnplacedFaces != null
                    && li < r.finalUnplacedFaces.Length)
                    ? r.finalUnplacedFaces[li] : 0;

                for (int i = 0; i < uv.Length; i++)
                    if (uv[i].x < -halfTexel || uv[i].x > 1f + halfTexel
                        || uv[i].y < -halfTexel || uv[i].y > 1f + halfTexel)
                        m.oobVerts++;

                var ownerFace = new int[res * res];
                for (int i = 0; i < ownerFace.Length; i++) ownerFace[i] = -1;
                var overlap = new bool[res * res];
                var pairSet = new HashSet<long>();

                var dens = new float[faceCount];
                var densArea = new float[faceCount];
                int densCount = 0;
                double sumTexArea = 0.0, sumArea3 = 0.0;

                for (int f = 0; f < faceCount; f++)
                {
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    Vector2 a = uv[i0], b = uv[i1], c = uv[i2];
                    float signed = 0.5f * ((b.x - a.x) * (c.y - a.y)
                                         - (c.x - a.x) * (b.y - a.y));
                    if (signed < 0f) m.invertedFaces++;
                    float uvArea = Mathf.Abs(signed);
                    if (uvArea < 1e-12f) { m.degenUvFaces++; continue; }

                    if (srcIdx != null)
                    {
                        int s0 = srcIdx[i0], s1 = srcIdx[i1], s2 = srcIdx[i2];
                        if (s0 < wv.Length && s1 < wv.Length && s2 < wv.Length)
                        {
                            float area3 = 0.5f * Vector3.Cross(
                                wv[s1] - wv[s0], wv[s2] - wv[s0]).magnitude;
                            if (area3 > 1e-12f)
                            {
                                float texArea = uvArea * res * res;
                                dens[densCount] = texArea / area3;
                                densArea[densCount] = area3;
                                densCount++;
                                sumTexArea += texArea;
                                sumArea3 += area3;
                            }
                        }
                    }

                    // Texel-centre ownership raster (both windings welcome —
                    // dividing by a negative denom flips the w signs back).
                    float ax = a.x * res, ay = a.y * res;
                    float bx = b.x * res, by = b.y * res;
                    float cx = c.x * res, cy = c.y * res;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(
                        Mathf.Min(ax, Mathf.Min(bx, cx)) - 0.5f));
                    int x1 = Mathf.Min(res - 1, Mathf.CeilToInt(
                        Mathf.Max(ax, Mathf.Max(bx, cx)) - 0.5f));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(
                        Mathf.Min(ay, Mathf.Min(by, cy)) - 0.5f));
                    int y1 = Mathf.Min(res - 1, Mathf.CeilToInt(
                        Mathf.Max(ay, Mathf.Max(by, cy)) - 0.5f));
                    if (x1 < x0 || y1 < y0) continue;
                    float denom = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                    if (Mathf.Abs(denom) < 1e-9f) continue;
                    float invDenom = 1f / denom;

                    for (int y = y0; y <= y1; y++)
                    {
                        float py = y + 0.5f;
                        int row = y * res;
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = x + 0.5f;
                            float w1 = ((by - cy) * (px - cx)
                                      + (cx - bx) * (py - cy)) * invDenom;
                            if (w1 < 0f || w1 > 1f) continue;
                            float w2 = ((cy - ay) * (px - cx)
                                      + (ax - cx) * (py - cy)) * invDenom;
                            if (w2 < 0f || w1 + w2 > 1f) continue;
                            int t = row + x;
                            int of = ownerFace[t];
                            if (of < 0)
                            {
                                ownerFace[t] = f;
                                m.coveredTexels++;
                                continue;
                            }
                            if (of == f) continue;
                            bool sameShell = fShell[of] == fShell[f];
                            if (sameShell && FacesTouch(uv, tris, srcIdx, of, f, weldTol2))
                                continue;   // seam/fan tie — legitimate
                            if (!overlap[t])
                            {
                                overlap[t] = true;
                                m.overlapTexels++;
                            }
                            if (!sameShell && pairSet.Count < 4096)
                            {
                                long sa = fShell[of], sb = fShell[f];
                                pairSet.Add(sa < sb ? (sa << 32) | sb : (sb << 32) | sa);
                            }
                        }
                    }
                }

                m.utilizationPct = 100f * m.coveredTexels / (res * res);
                m.overlapPctOfCovered = m.coveredTexels > 0
                    ? 100f * m.overlapTexels / m.coveredTexels : 0f;
                m.overlapShellPairs = pairSet.Count;

                if (densCount > 0 && sumArea3 > 0.0)
                {
                    m.tpuMean = Mathf.Sqrt((float)(sumTexArea / sumArea3));
                    Array.Sort(dens, densArea, 0, densCount);
                    double acc = 0.0;
                    double lo = 0.01 * sumArea3, hi = 0.99 * sumArea3;
                    float p1 = dens[0], p99 = dens[densCount - 1];
                    bool gotLo = false;
                    for (int i = 0; i < densCount; i++)
                    {
                        acc += densArea[i];
                        if (!gotLo && acc >= lo) { p1 = dens[i]; gotLo = true; }
                        if (acc >= hi) { p99 = dens[i]; break; }
                    }
                    m.tpuP1 = Mathf.Sqrt(p1);
                    m.tpuP99 = Mathf.Sqrt(p99);
                    m.tpuSpread = m.tpuP1 > 1e-6f ? m.tpuP99 / m.tpuP1 : 0f;
                }

                var pxBuf = new Color32[res * res];
                var colEmpty = new Color32(12, 12, 12, 255);
                var colCov = new Color32(96, 96, 96, 255);
                var colOver = new Color32(255, 48, 48, 255);
                for (int t = 0; t < pxBuf.Length; t++)
                    pxBuf[t] = overlap[t] ? colOver
                        : (ownerFace[t] >= 0 ? colCov : colEmpty);
                r.stageEOverlapPx[li] = pxBuf;

                ownerFaceByLod[li] = ownerFace;
                r.stageEMetrics[li] = m;
            }

            // Cross-LOD containment: every texel of a group whose canonical
            // lives on ANOTHER LOD must fall inside that canonical LOD's
            // footprint for the same group (3×3 dilated to absorb raster
            // noise). This is the cascade's contract — one bake valid across
            // all LODs — measured instead of assumed.
            int nG = r.groups.Length;
            var totByGroup = new int[nG];
            var inByGroup = new int[nG];
            for (int li = 0; li < lodCount; li++)
            {
                var ownerFace = ownerFaceByLod[li];
                var fGroup = (li < r.finalFaceGroup.Length) ? r.finalFaceGroup[li] : null;
                if (ownerFace == null || fGroup == null) continue;
                Array.Clear(totByGroup, 0, nG);
                Array.Clear(inByGroup, 0, nG);

                for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    int f = ownerFace[y * res + x];
                    if (f < 0 || f >= fGroup.Length) continue;
                    int g = fGroup[f];
                    if (g < 0 || g >= nG) continue;
                    int cl = r.groups[g].canonicalLod;
                    if (cl == li || cl < 0 || cl >= lodCount) continue;
                    var canonOwner = ownerFaceByLod[cl];
                    var canonGroup = (cl < r.finalFaceGroup.Length)
                        ? r.finalFaceGroup[cl] : null;
                    if (canonOwner == null || canonGroup == null) continue;
                    totByGroup[g]++;
                    bool inside = false;
                    for (int dy = -1; dy <= 1 && !inside; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= res) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= res) continue;
                            int cf = canonOwner[ny * res + nx];
                            if (cf >= 0 && cf < canonGroup.Length && canonGroup[cf] == g)
                            { inside = true; break; }
                        }
                    }
                    if (inside) inByGroup[g]++;
                }

                int tot = 0, contained = 0, misaligned = 0;
                for (int g = 0; g < nG; g++)
                {
                    tot += totByGroup[g];
                    contained += inByGroup[g];
                    if (totByGroup[g] >= 16 && inByGroup[g] * 2 < totByGroup[g])
                        misaligned++;
                }
                var mm = r.stageEMetrics[li];
                mm.xLodTexels = tot;
                mm.xLodContainedPct = tot > 0 ? 100f * contained / tot : 100f;
                mm.misalignedGroups = misaligned;
                r.stageEMetrics[li] = mm;

                UvtLog.Info(UvtLog.Category.Benchmark,
                    $"[HierRepack] Stage E3: '{lg.name}' LOD{li} — "
                    + $"faces={mm.faces}/{mm.srcFaces} unplaced={mm.unplacedFaces} "
                    + $"inverted={mm.invertedFaces} degenUv={mm.degenUvFaces} "
                    + $"oobVerts={mm.oobVerts} | util={mm.utilizationPct:F1}% "
                    + $"overlap={mm.overlapTexels}px ({mm.overlapPctOfCovered:F2}% of covered, "
                    + $"{mm.overlapShellPairs} shell pairs) | tpu mean={mm.tpuMean:F1} "
                    + $"p1={mm.tpuP1:F1} p99={mm.tpuP99:F1} spread={mm.tpuSpread:F2}x "
                    + $"| xLOD={mm.xLodTexels}px contained={mm.xLodContainedPct:F1}% "
                    + $"misalignedGroups={mm.misalignedGroups}");
            }
        }

        // ─── PR-3 Stage 6: bake the new uv2 into cloned LOD meshes ────

        /// <summary>Pick two orthonormal vectors in the plane normal to <paramref name="n"/>.
        /// Algorithm: take any axis not parallel to n, cross to get basisU, cross again for basisV.
        /// Used by <see cref="ExtractShells"/> when materialising 3D shells with a local basis.</summary>
        static void ComputePlaneBasis(Vector3 n, out Vector3 u, out Vector3 v)
        {
            Vector3 helper = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            u = Vector3.Cross(n, helper).normalized;
            v = Vector3.Cross(n, u).normalized;
        }

        /// <summary>Project every CORNER VERTEX of every face in the shell onto
        /// (u,v) basis centred at <paramref name="origin"/>; max abs value along
        /// each axis becomes the half-extent. Vertex-based (not centroid-based)
        /// because the projector will project the SAME vertices into the
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
            // have zero extent and divide-by-zero in any downstream projector.
            const float kMinExtent = 1e-4f;
            extU = Mathf.Max(maxU, kMinExtent);
            extV = Mathf.Max(maxV, kMinExtent);
        }

        /// <summary>For every fine LOD that produced a Stage-5 final UV2,
        /// clone the source mesh with the seam-vertex split applied:
        /// each output vertex copies positions / normals / tangents /
        /// uv0..uv7 / colors from <c>mesh.vertices[finalSourceVertexIdx[i]]</c>,
        /// uv2 from <c>finalUv2[li]</c>, triangles from <c>finalTris[li]</c>.
        /// Bounds are recomputed; normals / tangents are NOT recalculated
        /// when the source had them (we keep the authored values, just
        /// duplicated). Output meshes land on <c>r.finalMeshes[li]</c>.
        /// In-memory only — the menu-driven Apply step swaps them into
        /// renderers via Undo; without Apply they're orphaned and the
        /// next GC cycle reclaims them.</summary>
        public static void BuildFinalMeshes(LODGroup lg, Result r)
        {
            if (lg == null) return;
            if (r.finalUv2 == null || r.finalTris == null || r.finalSourceVertexIdx == null)
                return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            r.finalMeshes = new Mesh[lodCount];

            var uvBuf = new List<Vector2>();
            for (int li = 0; li < lodCount; li++)
            {
                if (li >= r.finalUv2.Length) break;
                var newUv2 = r.finalUv2[li];
                var newTris = r.finalTris[li];
                var srcIdx = r.finalSourceVertexIdx[li];
                if (newUv2 == null || newTris == null || srcIdx == null) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var src = mf != null ? mf.sharedMesh : null;
                if (src == null) continue;

                int outVc = srcIdx.Length;
                var clone = new Mesh
                {
                    name = src.name + "_hierUv2",
                    indexFormat = outVc >= 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : src.indexFormat,
                };

                // vertices — required, drives output vertex count.
                var srcVerts = src.vertices;
                var newVerts = new Vector3[outVc];
                for (int i = 0; i < outVc; i++) newVerts[i] = srcVerts[srcIdx[i]];
                clone.vertices = newVerts;

                // Optional attributes — only copy when the source has
                // them sized to the source vertex count (skinned meshes
                // with extra channels are caller's problem; we don't
                // touch boneWeights / bindposes here).
                var srcNormals = src.normals;
                if (srcNormals != null && srcNormals.Length == srcVerts.Length)
                {
                    var newN = new Vector3[outVc];
                    for (int i = 0; i < outVc; i++) newN[i] = srcNormals[srcIdx[i]];
                    clone.normals = newN;
                }

                var srcTangents = src.tangents;
                if (srcTangents != null && srcTangents.Length == srcVerts.Length)
                {
                    var newT = new Vector4[outVc];
                    for (int i = 0; i < outVc; i++) newT[i] = srcTangents[srcIdx[i]];
                    clone.tangents = newT;
                }

                var srcColors = src.colors32;
                if (srcColors != null && srcColors.Length == srcVerts.Length)
                {
                    var newC = new Color32[outVc];
                    for (int i = 0; i < outVc; i++) newC[i] = srcColors[srcIdx[i]];
                    clone.colors32 = newC;
                }

                // UV channels 0, 2..7 (1 == uv2 is replaced below).
                for (int ch = 0; ch < 8; ch++)
                {
                    if (ch == 1) continue;
                    uvBuf.Clear();
                    src.GetUVs(ch, uvBuf);
                    if (uvBuf.Count == 0 || uvBuf.Count != srcVerts.Length) continue;
                    var newUv = new List<Vector2>(outVc);
                    for (int i = 0; i < outVc; i++) newUv.Add(uvBuf[srcIdx[i]]);
                    clone.SetUVs(ch, newUv);
                }

                // The new lightmap UV.
                clone.uv2 = newUv2;

                // Triangles last (indexFormat / vertex count must be
                // set before this call).
                clone.triangles = newTris;
                clone.RecalculateBounds();

                r.finalMeshes[li] = clone;
            }
        }

        /// <summary>PR-3 Stage B visualisation: render the classical
        /// xatlas unwrap of each non-deepest LOD as a standalone layout
        /// PNG. Side-by-side with proxy_uv2_active.png the operator
        /// can confirm each LOD unwraps into a clean layout with the
        /// expected shell orientation; Stage D's cascade projection
        /// will fold these into the per-LOD final UV.</summary>
        static void WriteFineClassicalUv2Pngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.fineClassicalUv2 == null || r.fineClassicalTris == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            for (int li = 0; li < lodCount; li++)
            {
                if (li >= r.fineClassicalUv2.Length) break;
                var uv = r.fineClassicalUv2[li];
                var tr = r.fineClassicalTris[li];
                if (uv == null || tr == null || tr.Length < 3) continue;
                string path = Path.Combine(outputDir, $"lod{li}_classical_uv2.png");
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

        /// <summary>Hash-based hue palette for diagnostic PNGs whose
        /// "label" is an integer id with no semantic ordering (shellId,
        /// groupId). Knuth multiplicative hash spreads adjacent ids
        /// across the colour wheel, magenta sentinel for -1.</summary>
        static Color32 LabelColor(int id)
        {
            if (id < 0) return new Color32(255, 40, 200, 255); // unassigned / degen
            uint h = unchecked((uint)id * 2654435761u);
            float hue = (h % 360u) / 360f;
            // Slightly desaturated + medium-bright so the white wire
            // outlines from UvPngWriter / RasterizeTrianglePx are still
            // readable on top.
            Color c = Color.HSVToRGB(hue, 0.55f, 0.85f);
            return new Color32(
                (byte)Mathf.Clamp(c.r * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.g * 255f, 0f, 255f),
                (byte)Mathf.Clamp(c.b * 255f, 0f, 255f), 255);
        }

        /// <summary>Stage C visualisation: render each LOD as a 3D
        /// isometric view with faces shaded by their shellId. Same
        /// projector + rasterizer used by WriteProxyHitsPngs. One PNG
        /// per LOD: <c>lod{N}_shells.png</c>.</summary>
        static void WritePerLodShellsPngs(string outputDir, LODGroup lg, Result r)
        {
            if (r.perLodShells == null || r.perLodFaceToShell == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            for (int li = 0; li < lodCount; li++)
            {
                if (li >= r.perLodShells.Length) break;
                var faceToShell = r.perLodFaceToShell[li];
                if (faceToShell == null) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                var localVerts = mesh.vertices;
                var tris = mesh.triangles;
                int faceCount = tris.Length / 3;
                if (faceCount == 0 || faceCount != faceToShell.Length) continue;
                var xform = rs[0].transform;

                // World-space vertex transform + AABB for iso viewport.
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
                    Color32 col = LabelColor(faceToShell[f]);
                    Vector2 a = IsoProject(worldVerts[tris[f * 3]],     isoR, isoU, midU, midV, scale, half);
                    Vector2 b = IsoProject(worldVerts[tris[f * 3 + 1]], isoR, isoU, midU, midV, scale, half);
                    Vector2 c = IsoProject(worldVerts[tris[f * 3 + 2]], isoR, isoU, midU, midV, scale, half);
                    RasterizeTrianglePx(pixels, size, a, b, c, col);
                }

                string path = Path.Combine(outputDir, $"lod{li}_shells.png");
                EncodePng(pixels, size, size, path);
            }
        }

        /// <summary>Stage D diagnostic: same iso-view as lod{N}_shells.png
        /// but each face is coloured by its lighting-domain GROUP id
        /// instead of its per-LOD shell id. Because <see cref="LabelColor"/>
        /// is a pure hash of the integer id, a group that spans several
        /// LODs gets the SAME colour on every LOD where it appears — that's
        /// the visual cue that confirms the cascade converged. Unmatched
        /// finer shells get fresh colours that don't appear on the deeper
        /// PNGs. Written to lod{N}_groups.png.</summary>
        static void WritePerLodGroupsPngs(string outputDir, LODGroup lg, Result r,
            string fileSuffix = "")
        {
            if (r.perLodShells == null || r.perLodFaceToShell == null
                || r.perLodShellToGroup == null) return;
            var lods = lg.GetLODs();
            int lodCount = lods.Length;
            for (int li = 0; li < lodCount; li++)
            {
                if (li >= r.perLodShells.Length) break;
                var faceToShell  = r.perLodFaceToShell[li];
                var shellToGroup = r.perLodShellToGroup[li];
                if (faceToShell == null || shellToGroup == null) continue;
                var rs = lods[li].renderers;
                if (rs == null || rs.Length == 0 || rs[0] == null) continue;
                var mf = rs[0].GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                var localVerts = mesh.vertices;
                var tris = mesh.triangles;
                int faceCount = tris.Length / 3;
                if (faceCount == 0 || faceCount != faceToShell.Length) continue;
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
                    int sh = faceToShell[f];
                    int gid = (sh >= 0 && sh < shellToGroup.Length) ? shellToGroup[sh] : -1;
                    Color32 col = LabelColor(gid);
                    Vector2 a = IsoProject(worldVerts[tris[f * 3]],     isoR, isoU, midU, midV, scale, half);
                    Vector2 b = IsoProject(worldVerts[tris[f * 3 + 1]], isoR, isoU, midU, midV, scale, half);
                    Vector2 c = IsoProject(worldVerts[tris[f * 3 + 2]], isoR, isoU, midU, midV, scale, half);
                    RasterizeTrianglePx(pixels, size, a, b, c, col);
                }

                string path = Path.Combine(outputDir, $"lod{li}_groups{fileSuffix}.png");
                EncodePng(pixels, size, size, path);
            }
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


        static Vector2 IsoProject(Vector3 p, Vector3 isoR, Vector3 isoU,
            float midU, float midV, float scale, float half)
        {
            float pu = Vector3.Dot(p, isoR);
            float pv = Vector3.Dot(p, isoU);
            return new Vector2(half + (pu - midU) * scale,
                               half - (pv - midV) * scale);
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

    }
}
