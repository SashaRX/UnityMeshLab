// HierarchicalRepack.cs — builder for the inverse-hierarchical UV2 atlas.
//
// Phase A of the new pipeline. Read-only — does NOT write mesh.uv2 yet
// (that's InverseTransfer in PR-3). Just produces the per-mesh-per-face
// → LightingDomain assignment + the atlas layout that InverseTransfer
// will project into.
//
// Pipeline (PR-2.5 — shell-level classification):
//   1. Pick the deepest LOD from the LODGroup as the "base" — its shells
//      define the lighting domains that finer LODs will share.
//   2. Extract 3D shells on the deepest LOD via union-find on face
//      adjacency + normal threshold (≤30° — matches probe v2/v3 + xatlas
//      hard-edge analysis convention). Adjacency uses CANONICAL vertex
//      indices (deduplicated by world-space position) so UV/normal seams
//      in Unity's mesh.vertices don't fragment a single physical surface
//      into many single-tri shells. Degenerate (area<1e-12) tris are
//      skipped entirely (faceToDomain = -1).
//   3. For each finer LOD: extract its OWN shells (same dedup + threshold),
//      then classify each fine SHELL (not face) vs the base shells:
//        Overlay  — fine shell aligned with parent (angle ≤ overlayAngleDeg)
//                   AND lies in parent's plane (perpNorm ≤ overlayPerpNorm)
//                   AND fits within parent's planar extent → reuse parent's
//                   atlas rect, no new domain. Wall+sign, box+decal.
//        Skip     — tiny shell (area below skipAreaFrac AND face count low)
//                   that's not overlay-eligible → faceToDomain = -1.
//                   Handles, knobs, geometric noise. No atlas slot.
//        Promote  — everything else: own direction, significant area →
//                   becomes its own atlas domain.
//   4. Build LightingDomain[]: one per base shell + one per promoted
//      fine shell. Overlaid/skipped faces don't create domains.
//   5. Pack atlas rects. PR-2 used a naive horizontal-strip packer; PR-2.5
//      keeps it (still a placeholder) but with dramatically fewer + better
//      shells. PR-2.6 swaps xatlas via XatlasNative wrapper.
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
using UnityEditor;
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

            /// <summary>Promote face if angle to parent shell's dominant normal exceeds
            /// this (degrees). Default 60 ≈ 50% area retained under orthographic
            /// projection — the "marginal acceptable" boundary.</summary>
            public float promoteAngleDeg;

            /// <summary>Promote face if its 3D area is more than this fraction of the
            /// parent shell's total 3D area. Indicates misidentified parent (probe v2
            /// data showed Wooden_Box_Long has 8.1% of faces in this bucket).</summary>
            public float promoteRatio;

            /// <summary>Promote face if its centroid sits further than this fraction of
            /// the deepest-LOD mesh diagonal from the parent shell's plane (along the
            /// shell's normal). Catches "floating geometry" — bolts, free-standing
            /// detail. Scale-invariant.</summary>
            public float promotePerpNorm;

            /// <summary>Legacy K-nearest filter from probe v3's per-face classifier.
            /// PR-2.5 shell-level matching uses a full scan instead — see notes
            /// in <c>ClassifyFineShell</c>. Kept in Options for forward compat
            /// with any per-face diagnostics that still rely on it.</summary>
            public int shellSearchK;

            /// <summary>Target atlas resolution (pixels). Final atlas may be slightly
            /// larger if shells don't fit; naive packer in PR-2 grows the height.</summary>
            public int atlasResolutionPx;

            /// <summary>Padding between domains in atlas pixels. Intra-domain overlap
            /// is intentional (shared lighting domain feature — see EXPERIMENTS.md);
            /// padding only applies BETWEEN domains, not within.</summary>
            public int interDomainPaddingPx;

            /// <summary>Overlay if fine shell's dominant normal is within this many
            /// degrees of the parent base shell's normal. Stricter than
            /// promoteAngleDeg so that faces NOT promoted but with non-trivial
            /// angular drift still get their own (promoted) domain.</summary>
            public float overlayAngleDeg;

            /// <summary>Overlay if fine shell's centroid lies within this fraction of
            /// the deepest-LOD mesh diagonal of the parent base shell's plane.
            /// Tighter than promotePerpNorm — overlay requires the detail to actually
            /// lie ON the parent surface, not just be near it.</summary>
            public float overlayPerpNorm;

            /// <summary>Overlay tolerance: fine shell's planar extent (in parent's
            /// basis) must be within (1 + overlayExtentSlack) × parent extent.
            /// 0.10 = a 10% overhang is still allowed.</summary>
            public float overlayExtentSlack;

            /// <summary>Skip a fine shell if BOTH (a) its 3D area is below this
            /// fraction of the total deepest-LOD area AND (b) its face count
            /// is at or below <see cref="skipMaxFaceCount"/>. Handles,
            /// fasteners, and other small geometric noise where allocating
            /// any atlas space is wasteful.</summary>
            public float skipAreaFrac;

            /// <summary>Companion to <see cref="skipAreaFrac"/> — a shell with
            /// many faces always promotes even if its total area is small,
            /// because face count alone implies someone will see it.</summary>
            public int skipMaxFaceCount;

            public static Options Default => new Options
            {
                shellNormalThresholdDeg = 30f,
                promoteAngleDeg         = 60f,
                promoteRatio            = 1.5f,
                promotePerpNorm         = 0.05f,
                shellSearchK            = 10,
                atlasResolutionPx       = 1024,
                interDomainPaddingPx    = 4,
                overlayAngleDeg         = 35f,
                overlayPerpNorm         = 0.02f,
                overlayExtentSlack      = 0.30f,
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
                out int[] deepCanonicalTris, out deepDegen);
            var deepFaceToShell = new int[deepFaces.Length];
            var deepShells = ExtractShells(deepFaces, deepCanonicalTris,
                opts.shellNormalThresholdDeg, deepFaceToShell);
            float totalDeepArea = 0f;
            for (int si = 0; si < deepShells.Length; si++) totalDeepArea += deepShells[si].totalArea;
            if (totalDeepArea < 1e-12f) totalDeepArea = 1e-12f;

            // ── Step 3: per-LOD shell-level classification ──
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

            // Fine LODs: extract shells on each LOD, classify per shell.
            var promotedClusters = new List<PromotedCluster>();
            int totalFineFaces = 0, promotedFineFaces = 0,
                overlaidFineFaces = 0, skippedFineFaces = 0, degenFineFaces = deepDegen;
            // Note: degenFineFaces is a slight misnomer — it includes the deepest
            // LOD's degenerates too, so the report can show "all degenerate tris
            // dropped from atlas" in one number.

            for (int li = 0; li < deepest; li++)
            {
                if (meshes[li] == null) continue;
                int fineDegen;
                var fineFaces = BuildFaceData(meshes[li], xforms[li], meshDiag,
                    out int[] fineCanonicalTris, out fineDegen);
                degenFineFaces += fineDegen;
                totalFineFaces += fineFaces.Length;

                var fineFaceToShell = new int[fineFaces.Length];
                var fineShells = ExtractShells(fineFaces, fineCanonicalTris,
                    opts.shellNormalThresholdDeg, fineFaceToShell);

                // Per-shell decision. All faces in a shell get the same fate.
                var shellDomain = new int[fineShells.Length];   // -1 = skip; ≥0 = domain index
                for (int s = 0; s < fineShells.Length; s++) shellDomain[s] = -1;

                for (int s = 0; s < fineShells.Length; s++)
                {
                    var decision = ClassifyFineShell(fineShells[s], deepShells, opts,
                        meshDiag, totalDeepArea);
                    switch (decision.kind)
                    {
                        case ShellDecisionKind.Overlay:
                            shellDomain[s] = decision.parentBaseShellIdx;
                            overlaidFineFaces += fineShells[s].faceCount;
                            break;
                        case ShellDecisionKind.Promote:
                            int domainIdx = baseN + promotedClusters.Count;
                            shellDomain[s] = domainIdx;
                            promotedClusters.Add(MakeClusterFromShell(fineShells[s], li));
                            promotedFineFaces += fineShells[s].faceCount;
                            break;
                        case ShellDecisionKind.Skip:
                        default:
                            skippedFineFaces += fineShells[s].faceCount;
                            break;
                    }
                }

                for (int f = 0; f < fineFaces.Length; f++)
                {
                    int s = fineFaceToShell[f];
                    if (s < 0) continue; // degenerate face — leave -1
                    result.faceToDomain[li][f] = shellDomain[s];
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
            // world unit" target. PR-2.6 will replace this with xatlas via the
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

        enum ShellDecisionKind
        {
            Skip = 0,     // tiny / noisy geometric detail — no atlas slot
            Overlay = 1,  // aligned with parent base shell — reuse parent's rect
            Promote = 2,  // own direction / area — gets its own atlas domain
        }

        struct ShellDecision
        {
            public ShellDecisionKind kind;
            public int parentBaseShellIdx; // only meaningful when kind == Overlay
        }

        // ─── Face data + diagonal ────────────────────────────────────

        /// <summary>Build per-face data for one mesh. Out-params:
        /// <paramref name="canonicalTris"/> rewrites mesh.triangles using
        /// position-deduplicated vertex indices (epsilon = meshDiag × 1e-5)
        /// so adjacent triangles split by Unity UV/normal seams still share
        /// edges — without this, ExtractShells sees ~3× too many shells on
        /// curved geometry. <paramref name="degenerateCount"/> reports tris
        /// dropped (their Face3D.area is set to 0 so callers can skip them
        /// via faceToDomain = -1 instead of producing zero-area shells).</summary>
        static Face3D[] BuildFaceData(Mesh mesh, Transform xform, float meshDiag,
            out int[] canonicalTris, out int degenerateCount)
        {
            var localVerts = mesh.vertices;
            var verts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                verts[i] = xform.TransformPoint(localVerts[i]);
            var tris = mesh.triangles;
            canonicalTris = BuildCanonicalIndices(verts, tris, meshDiag);

            int n = tris.Length / 3;
            var data = new Face3D[n];
            int degenerate = 0;
            for (int f = 0; f < n; f++)
            {
                var a = verts[tris[f * 3]];
                var b = verts[tris[f * 3 + 1]];
                var c = verts[tris[f * 3 + 2]];
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

        static Shell3D[] ExtractShells(Face3D[] faces, int[] canonicalTris, float thresholdDeg)
        {
            var faceToShell = new int[faces.Length];
            return ExtractShells(faces, canonicalTris, thresholdDeg, faceToShell);
        }

        /// <summary>Variant that also fills <paramref name="faceToShellOut"/> with the
        /// shell index per face (or -1 for degenerate faces, which are excluded
        /// from shell formation). The array must be pre-allocated to faces.Length.
        /// <paramref name="canonicalTris"/> must contain position-deduplicated
        /// vertex indices (see <see cref="BuildCanonicalIndices"/>).</summary>
        static Shell3D[] ExtractShells(Face3D[] faces, int[] canonicalTris, float thresholdDeg, int[] faceToShellOut)
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
                if (faces[f].area <= 0f)
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
                ComputeExtents(faces, faces_[si], shells[si].centroid, shells[si].basisU, shells[si].basisV,
                    out shells[si].extentU, out shells[si].extentV);
            }
            return shells;
        }

        // ─── Shell-level classification (PR-2.5) ─────────────────────

        /// <summary>
        /// Decide what to do with a fine-LOD shell: overlay onto a parent base
        /// shell, promote it as its own atlas domain, or skip (no atlas slot).
        ///
        /// Selection rules (in order):
        ///   1. Find best parent (K-nearest by centroid, then min angle).
        ///   2. If shell is angularly aligned (≤ overlayAngleDeg) AND lies in
        ///      parent's plane (perpNorm ≤ overlayPerpNorm) AND fits within
        ///      parent's planar extent (with overlayExtentSlack tolerance) →
        ///      Overlay. The fine shell's faces will read the parent's atlas
        ///      texels, so a wall-mounted sign inherits the wall's lightmap.
        ///   3. If shell is small (area &lt; skipAreaFrac × totalDeepArea AND
        ///      face count ≤ skipMaxFaceCount) → Skip. Handles, fasteners,
        ///      geometric noise — wasting atlas space on them is pointless.
        ///   4. Otherwise → Promote. The shell becomes its own domain.
        /// </summary>
        static ShellDecision ClassifyFineShell(Shell3D fine, Shell3D[] baseShells,
            Options opts, float meshDiag, float totalDeepArea)
        {
            if (baseShells.Length == 0)
                return new ShellDecision { kind = ShellDecisionKind.Promote, parentBaseShellIdx = -1 };

            // Combined search: scan ALL base shells, but instead of picking the
            // globally-best-angle parent (which on curved/repeated geometry will
            // be some perfectly aligned shell on the FAR side of the mesh —
            // angle=0 but distance huge → extent-fit fail → promote regression),
            // we filter to angularly-eligible parents AND keep only those that
            // also pass perpendicular-distance + extent-fit. Among those, prefer
            // the one with the smallest angle. If none pass, promote based on
            // the overall best-angle shell so the domain still has a sensible
            // dominant-normal record for downstream packing.
            //
            // For Skip-detection we use the cheapest pass: tiny area + low face
            // count. That's independent of parent search.
            //
            // Cost is O(N base × M fine) — ~270 × ~600 = 160k shell tests on
            // the worst test model; each test is a few float ops, microseconds.
            float areaFrac = fine.totalArea / totalDeepArea;
            bool tinyArea  = areaFrac < opts.skipAreaFrac;
            bool fewFaces  = fine.faceCount <= opts.skipMaxFaceCount;
            if (tinyArea && fewFaces)
                return new ShellDecision { kind = ShellDecisionKind.Skip, parentBaseShellIdx = -1 };

            int bestOverlayShell = -1;
            float bestOverlayAngle = float.MaxValue;
            int bestPromoteShell = -1;
            float bestPromoteAngle = float.MaxValue;

            for (int si = 0; si < baseShells.Length; si++)
            {
                var parent = baseShells[si];

                float dot = Vector3.Dot(fine.dominantNormal, parent.dominantNormal);
                if (dot >  1f) dot =  1f;
                if (dot < -1f) dot = -1f;
                float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;

                // Track overall best-angle for the promote fallback.
                if (angle < bestPromoteAngle) { bestPromoteAngle = angle; bestPromoteShell = si; }

                if (angle > opts.overlayAngleDeg) continue;

                // Perpendicular-distance test in parent's plane.
                float perpAbs = Mathf.Abs(Vector3.Dot(fine.centroid - parent.centroid,
                    parent.dominantNormal));
                float perpNorm = perpAbs / meshDiag;
                if (perpNorm > opts.overlayPerpNorm) continue;

                // Planar extent fit in parent's basis (with slack).
                Vector3 d = fine.centroid - parent.centroid;
                float du = Mathf.Abs(Vector3.Dot(d, parent.basisU));
                float dv = Mathf.Abs(Vector3.Dot(d, parent.basisV));
                float boundU = parent.extentU * (1f + opts.overlayExtentSlack);
                float boundV = parent.extentV * (1f + opts.overlayExtentSlack);
                float fitU = du + fine.extentU;
                float fitV = dv + fine.extentV;
                if (fitU > boundU || fitV > boundV) continue;

                // This parent passes all three tests. Keep the smallest-angle
                // candidate among the eligible set (ties broken by first-seen).
                if (angle < bestOverlayAngle)
                {
                    bestOverlayAngle = angle;
                    bestOverlayShell = si;
                }
            }

            if (bestOverlayShell >= 0)
                return new ShellDecision
                {
                    kind = ShellDecisionKind.Overlay,
                    parentBaseShellIdx = bestOverlayShell,
                };

            return new ShellDecision
            {
                kind = ShellDecisionKind.Promote,
                parentBaseShellIdx = bestPromoteShell,
            };
        }

        /// <summary>Wrap a fine-LOD shell into a <see cref="PromotedCluster"/>
        /// for the unified domain table. The shell already has area-weighted
        /// plane data + extents — just copy fields and tag the source LOD.</summary>
        static PromotedCluster MakeClusterFromShell(Shell3D shell, int sourceLodIndex)
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

        /// <summary>Project each face centroid onto (u,v) basis centred at <paramref name="origin"/>;
        /// max abs value along each axis becomes the half-extent. Defines the domain's
        /// "rectangle in plane space" that we'll later squeeze into its atlas rect.</summary>
        static void ComputeExtents(Face3D[] faces, List<int> faceIndices, Vector3 origin,
            Vector3 u, Vector3 v, out float extU, out float extV)
        {
            float maxU = 0f, maxV = 0f;
            foreach (int f in faceIndices)
            {
                Vector3 d = faces[f].centroid - origin;
                float pu = Mathf.Abs(Vector3.Dot(d, u));
                float pv = Mathf.Abs(Vector3.Dot(d, v));
                if (pu > maxU) maxU = pu;
                if (pv > maxV) maxV = pv;
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
        /// PR-2.6 swaps this for xatlas via XatlasNative. The contract is the same:
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

        // ─── Editor entry: dry-run menu item ─────────────────────────

        const string DryRunMenuPath = "Mesh Lab/Diag/Hierarchical Atlas Dry-Run";

        [MenuItem(DryRunMenuPath, true)]
        static bool ValidateDryRun() => CollectSelectedLodGroups().Count > 0;

        [MenuItem(DryRunMenuPath)]
        static void DryRun()
        {
            var lgs = CollectSelectedLodGroups();
            if (lgs.Count == 0)
            {
                EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                    "Select one or more GameObjects under a LODGroup first.\n" +
                    "Prefab assets in the Project window also work.", "OK");
                return;
            }

            var opts = Options.Default;
            var failures = new List<string>();
            int ok = 0;
            string lastReport = null;
            Result lastResult = null;
            string lastName = null;
            for (int i = 0; i < lgs.Count; i++)
            {
                var lg = lgs[i];
                if (lgs.Count > 1)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Hierarchical Atlas Dry-Run",
                            $"[{i + 1}/{lgs.Count}] {lg.name}",
                            (float)i / lgs.Count))
                    {
                        UvtLog.Warn(UvtLog.Category.Benchmark,
                            $"[HierRepack] Batch dry-run cancelled at {i}/{lgs.Count}.");
                        break;
                    }
                }
                try
                {
                    var result = Build(lg, opts);
                    if (!string.IsNullOrEmpty(result.error))
                    {
                        failures.Add($"{lg.name}: {result.error}");
                        continue;
                    }
                    string reportPath = WriteDryRunReport(lg.name, result);
                    LogDryRunSummary(lg.name, result);
                    lastReport = reportPath;
                    lastResult = result;
                    lastName   = lg.name;
                    ok++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{lg.name}: {ex.Message}");
                    UvtLog.Error(UvtLog.Category.Benchmark,
                        $"[HierRepack] Dry-run threw on '{lg.name}': {ex}");
                }
            }
            EditorUtility.ClearProgressBar();

            // Single-LODGroup: detailed dialog like before. Batch: terse summary
            // pointing the operator at the per-model CSVs in BenchmarkReports/.
            if (lgs.Count == 1 && lastResult != null)
            {
                int denom = Mathf.Max(1, lastResult.totalFineFaces);
                EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                    $"Build complete on '{lastName}'.\n\n" +
                    $"Domains: {lastResult.domains.Length} " +
                    $"({lastResult.baseShellCount} base + {lastResult.promotedClusterCount} promoted)\n" +
                    $"Atlas: {lastResult.atlasPixelWidth}×{lastResult.atlasPixelHeight}px\n" +
                    $"Fine faces: {lastResult.totalFineFaces}\n" +
                    $"  promoted: {lastResult.promotedFineFaces} ({100f * lastResult.promotedFineFaces / denom:F1}%)\n" +
                    $"  overlaid: {lastResult.overlaidFineFaces} ({100f * lastResult.overlaidFineFaces / denom:F1}%)\n" +
                    $"  skipped:  {lastResult.skippedFineFaces} ({100f * lastResult.skippedFineFaces / denom:F1}%)\n" +
                    $"  degenerate: {lastResult.degenerateFineFaces}\n\n" +
                    $"Report: {lastReport}\n\nSee console for details.", "OK");
            }
            else
            {
                string failBlock = failures.Count == 0
                    ? ""
                    : "\n\nFailures:\n  " + string.Join("\n  ", failures);
                EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                    $"Batch complete: {ok}/{lgs.Count} models succeeded.\n\n" +
                    $"Per-model CSVs in BenchmarkReports/ (one file per LODGroup).\n" +
                    $"See console for per-model summaries." + failBlock,
                    "OK");
            }
        }

        /// <summary>Resolve every LODGroup reachable from the current Selection:
        /// scene GameObjects walk up via GetComponentInParent; Project-window
        /// prefab assets are loaded and searched via GetComponentInChildren.
        /// Deduplicated by LODGroup instance so selecting multiple children of
        /// the same LODGroup doesn't process it twice.</summary>
        static List<LODGroup> CollectSelectedLodGroups()
        {
            var result = new List<LODGroup>();
            var seen = new HashSet<LODGroup>();
            var sel = Selection.gameObjects;
            if (sel == null || sel.Length == 0) return result;
            foreach (var go in sel)
            {
                if (go == null) continue;
                // Scene object — climb to the nearest LODGroup ancestor.
                var lg = go.GetComponentInParent<LODGroup>();
                if (lg == null)
                {
                    // Project-window asset path: load the prefab and search
                    // its hierarchy (GetComponentInParent on a root prefab
                    // asset still returns null, but GetComponentInChildren
                    // walks descendants).
                    string assetPath = AssetDatabase.GetAssetPath(go);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        if (loaded != null) lg = loaded.GetComponentInChildren<LODGroup>(true);
                    }
                }
                if (lg != null && seen.Add(lg)) result.Add(lg);
            }
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
                $"(naive strip packer — PR-2.6 will swap in xatlas)");
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

        static string WriteDryRunReport(string lgName, Result r)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Application.dataPath;
            string dir = Path.Combine(projectRoot, "BenchmarkReports");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, $"hierrepack_{stamp}_{Sanitize(lgName)}.csv");
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
