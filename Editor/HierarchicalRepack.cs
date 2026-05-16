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

            public static Options Default => new Options
            {
                shellNormalThresholdDeg = 30f,
                atlasResolutionPx       = 1024,
                interDomainPaddingPx    = 4,
                overlayDistNorm         = 0.01f,
                skipAreaFrac            = 0.001f,
                skipMaxFaceCount        = 4,
            };
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
            public string error;
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
                deepCanonicalTris, opts.shellNormalThresholdDeg, deepFaceToShell, null);
            float totalDeepArea = 0f;
            for (int si = 0; si < deepShells.Length; si++) totalDeepArea += deepShells[si].totalArea;
            if (totalDeepArea < 1e-12f) totalDeepArea = 1e-12f;

            // Precompute deepest-LOD per-tri AABBs for the projector's
            // early-out filter (built once, used by every fine-LOD vertex
            // query across all fine LODs).
            BuildDeepAabbs(deepWorldVerts, deepRawTris, out var deepMin, out var deepMax);
            float overlayDistAbs = opts.overlayDistNorm * meshDiag;

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

                // Per-fine-vertex overlay shell: index into deepShells, or -1
                // if the vertex is too far from the proxy surface to overlay.
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

                // Per-fine-face decision: overlay if 3-corner consensus, else
                // mark for promote/skip clustering.
                var promoteMask = new bool[fineFaces.Length];
                var faceOverlayShell = new int[fineFaces.Length];
                for (int f = 0; f < fineFaces.Length; f++)
                {
                    faceOverlayShell[f] = -1;
                    if (fineFaces[f].area <= 0f) continue; // degenerate
                    int v0 = fineRawTris[f * 3];
                    int v1 = fineRawTris[f * 3 + 1];
                    int v2 = fineRawTris[f * 3 + 2];
                    int s0 = vertOverlayShell[v0];
                    int s1 = vertOverlayShell[v1];
                    int s2 = vertOverlayShell[v2];
                    if (s0 >= 0 && s0 == s1 && s1 == s2)
                    {
                        faceOverlayShell[f] = s0;
                        result.faceToDomain[li][f] = s0;
                        overlaidFineFaces++;
                    }
                    else
                    {
                        promoteMask[f] = true;
                    }
                }

                // Cluster the marked faces into shells using existing
                // adjacency logic (mask-filtered).
                var fineFaceToShell = new int[fineFaces.Length];
                var fineShells = ExtractShells(fineFaces, fineWorldVerts, fineRawTris,
                    fineCanonicalTris, opts.shellNormalThresholdDeg,
                    fineFaceToShell, promoteMask);

                // Per-cluster decision: tiny → Skip, else → Promote.
                for (int s = 0; s < fineShells.Length; s++)
                {
                    var shell = fineShells[s];
                    float areaFrac = shell.totalArea / totalDeepArea;
                    bool tinyArea  = areaFrac < opts.skipAreaFrac;
                    bool fewFaces  = shell.faceCount <= opts.skipMaxFaceCount;
                    int domainIdx;
                    if (tinyArea && fewFaces)
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
            int[] canonicalTris, float thresholdDeg)
        {
            var faceToShell = new int[faces.Length];
            return ExtractShells(faces, worldVerts, rawTris, canonicalTris, thresholdDeg,
                faceToShell, null);
        }

        /// <summary>Variant that also fills <paramref name="faceToShellOut"/> with the
        /// shell index per face (or -1 for degenerate faces / faces excluded by
        /// <paramref name="participateMask"/>). The array must be pre-allocated
        /// to faces.Length. <paramref name="canonicalTris"/> must contain
        /// position-deduplicated vertex indices (see <see cref="BuildCanonicalIndices"/>).
        /// <paramref name="participateMask"/> (optional, may be null): only faces
        /// with mask[f] == true participate in shell formation; the rest get
        /// faceToShellOut[f] = -1. Used to cluster the subset of fine-LOD faces
        /// flagged for promotion by the projective classifier.</summary>
        static Shell3D[] ExtractShells(Face3D[] faces, Vector3[] worldVerts, int[] rawTris,
            int[] canonicalTris, float thresholdDeg, int[] faceToShellOut,
            bool[] participateMask)
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
            return shells;
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
        /// <c>hier_repack.csv</c>. Returns the build <see cref="Result"/>; the
        /// caller can inspect counters or surface a per-case summary. This is
        /// the entry point used by <c>LightmapTransferTool.ExecBenchmark</c>;
        /// stand-alone single-model dry-runs are no longer wired to a
        /// dedicated menu — the unified benchmark covers that workflow.</summary>
        public static Result BuildAndWriteForCase(LODGroup lg, Options opts, string outputDir)
        {
            var result = Build(lg, opts);
            if (!string.IsNullOrEmpty(result.error)) return result;
            WriteDryRunReport(lg.name, result, outputDir);
            LogDryRunSummary(lg.name, result);
            return result;
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
            UvtLog.Info(UvtLog.Category.Benchmark, sb.ToString());
        }

        // Default path used by stand-alone callers (none remain in this PR,
        // kept for future ad-hoc invocations).
        static string WriteDryRunReport(string lgName, Result r)
            => WriteDryRunReport(lgName, r, null);

        /// <summary>Write the per-case dry-run CSV. If <paramref name="outputDir"/>
        /// is non-null the file lands as <c>{outputDir}/hier_repack.csv</c>;
        /// otherwise falls back to the timestamped BenchmarkReports/ layout
        /// used by stand-alone callers.</summary>
        static string WriteDryRunReport(string lgName, Result r, string outputDir)
        {
            string dir;
            string path;
            if (!string.IsNullOrEmpty(outputDir))
            {
                dir = outputDir;
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "hier_repack.csv");
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
