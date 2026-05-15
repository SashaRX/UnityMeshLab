// HierarchicalRepack.cs — builder for the inverse-hierarchical UV2 atlas.
//
// Phase A of the new pipeline. Read-only — does NOT write mesh.uv2 yet
// (that's InverseTransfer in PR-3). Just produces the per-mesh-per-face
// → LightingDomain assignment + the atlas layout that InverseTransfer
// will project into.
//
// Pipeline:
//   1. Pick the deepest LOD from the LODGroup as the "base" — its shells
//      define the lighting domains that finer LODs will share.
//   2. Extract 3D shells on the deepest LOD via union-find on face
//      adjacency + normal threshold (≤30° — matches probe v2/v3 + xatlas
//      hard-edge analysis convention).
//   3. For each finer LOD face: classify via probe v3 logic
//        promote if  shellAngle > opts.promoteAngleDeg
//                 OR shellRatio > opts.promoteRatio
//                 OR shellPerpDistanceNorm > opts.promotePerpNorm
//      Otherwise → assigned to its nearest-shell parent (K-nearest then
//      best-angle pick, exactly as the probe scored it).
//   4. Cluster promoted fine faces into mini-shells (union-find on
//      face-adjacency within each fine LOD) so the atlas-packer doesn't
//      have to deal with thousands of single-triangle charts.
//   5. Build LightingDomain[]: one per base shell + one per promoted
//      cluster. Each gets a 3D plane (centroid + normal + orthonormal
//      basis u,v) and a characteristic extent so InverseTransfer can
//      project any 3D vertex into the domain's atlas rect.
//   6. Pack atlas rects. PR-2 uses a naive horizontal-strip packer to
//      validate the data flow end-to-end without touching XatlasRepack;
//      PR-2.5 will swap in xatlas via the existing XatlasNative wrapper.
//      The packer choice doesn't affect the per-face domain assignment
//      that drives InverseTransfer — only the (x, y, w, h) coordinates.
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

            /// <summary>Number of nearest shells to consider per fine face when picking
            /// best-angle parent. K=10 from probe v2 — 1st-nearest by centroid is
            /// unreliable on curved deepest LODs.</summary>
            public int shellSearchK;

            /// <summary>Target atlas resolution (pixels). Final atlas may be slightly
            /// larger if shells don't fit; naive packer in PR-2 grows the height.</summary>
            public int atlasResolutionPx;

            /// <summary>Padding between domains in atlas pixels. Intra-domain overlap
            /// is intentional (shared lighting domain feature — see EXPERIMENTS.md);
            /// padding only applies BETWEEN domains, not within.</summary>
            public int interDomainPaddingPx;

            public static Options Default => new Options
            {
                shellNormalThresholdDeg = 30f,
                promoteAngleDeg         = 60f,
                promoteRatio            = 1.5f,
                promotePerpNorm         = 0.05f,
                shellSearchK            = 10,
                atlasResolutionPx       = 1024,
                interDomainPaddingPx    = 4,
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
            /// <summary>Per-LOD assignment: faceToDomain[lod][faceIdx] = domain index, or -1 if the face has no assignment (multi-renderer LOD or other skip path).</summary>
            public int[][] faceToDomain;
            public int    totalFineFaces;
            public int    promotedFineFaces;
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
            var deepFaces  = BuildFaceData(meshes[deepest], xforms[deepest]);
            var deepTris   = meshes[deepest].triangles;
            var deepShells = ExtractShells(deepFaces, deepTris, opts.shellNormalThresholdDeg);
            float meshDiag = ComputeMeshDiagonal(meshes[deepest], xforms[deepest]);
            if (meshDiag < 1e-6f) meshDiag = 1f;

            // ── Step 3 + 4: classify fine faces; group promoted into clusters ──
            // Per-LOD face → domain assignment. -1 = unassigned (skip).
            // We use a global domain-index numbering:
            //    [0 .. deepShells.Length-1]    → base shells (deepest LOD)
            //    [deepShells.Length .. end]    → promoted clusters
            int baseN = deepShells.Length;
            result.faceToDomain = new int[lodCount][];
            for (int li = 0; li < lodCount; li++)
            {
                int faceCount = (meshes[li] != null) ? meshes[li].triangles.Length / 3 : 0;
                var arr = new int[faceCount];
                for (int i = 0; i < faceCount; i++) arr[i] = -1;
                result.faceToDomain[li] = arr;
            }

            // The deepest LOD's own faces are trivially assigned to their
            // home shells — preserves the cross-LOD invariant that the
            // deepest LOD reads the same texels its shells were packed into.
            {
                var faceToShell = new int[deepFaces.Length];
                ExtractShells(deepFaces, deepTris, opts.shellNormalThresholdDeg, faceToShell);
                for (int f = 0; f < deepFaces.Length; f++)
                    result.faceToDomain[deepest][f] = faceToShell[f];
            }

            // For each FINER LOD, classify each face vs deepest shells, collect promotions.
            // Promotions are grouped by source LOD so we can cluster them within
            // their own LOD's topology (a face's adjacency is meaningful only
            // among faces of the same mesh).
            var promotedByLod = new List<int>[lodCount]; // list of face indices
            int totalFineFaces = 0, promotedFineFaces = 0;
            for (int li = 0; li < deepest; li++)
            {
                if (meshes[li] == null) continue;
                var fineFaces = BuildFaceData(meshes[li], xforms[li]);
                var fineTris  = meshes[li].triangles;
                promotedByLod[li] = new List<int>();
                for (int f = 0; f < fineFaces.Length; f++)
                {
                    totalFineFaces++;
                    var classify = ClassifyFineFace(fineFaces[f], deepShells, opts, meshDiag);
                    if (classify.promote)
                    {
                        promotedByLod[li].Add(f);
                        promotedFineFaces++;
                        // domain index assigned later, after clustering
                    }
                    else
                    {
                        result.faceToDomain[li][f] = classify.parentShellIdx;
                    }
                }
            }
            result.totalFineFaces    = totalFineFaces;
            result.promotedFineFaces = promotedFineFaces;

            // Cluster promoted faces per LOD. The cluster index is global —
            // domains [baseN, baseN+promotedClusterCount) — so InverseTransfer
            // can index the unified table.
            var promotedClusters = new List<PromotedCluster>();
            for (int li = 0; li < deepest; li++)
            {
                if (promotedByLod[li] == null || promotedByLod[li].Count == 0) continue;
                var fineFaces = BuildFaceData(meshes[li], xforms[li]);
                var fineTris  = meshes[li].triangles;
                var clusters = ClusterFaces(fineFaces, fineTris, promotedByLod[li], opts.shellNormalThresholdDeg);
                foreach (var cluster in clusters)
                {
                    int domainIdx = baseN + promotedClusters.Count;
                    foreach (int faceIdx in cluster.faceIndices)
                        result.faceToDomain[li][faceIdx] = domainIdx;
                    cluster.sourceLodIndex = li;
                    promotedClusters.Add(cluster);
                }
            }

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
            // world unit" target. PR-2.5 will replace this with xatlas via the
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

        struct ClassifyResult
        {
            public bool promote;
            public int  parentShellIdx;
        }

        // ─── Face data + diagonal ────────────────────────────────────

        static Face3D[] BuildFaceData(Mesh mesh, Transform xform)
        {
            var localVerts = mesh.vertices;
            var verts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                verts[i] = xform.TransformPoint(localVerts[i]);
            var tris = mesh.triangles;
            int n = tris.Length / 3;
            var data = new Face3D[n];
            for (int f = 0; f < n; f++)
            {
                var a = verts[tris[f * 3]];
                var b = verts[tris[f * 3 + 1]];
                var c = verts[tris[f * 3 + 2]];
                data[f].centroid = (a + b + c) / 3f;
                var cross = Vector3.Cross(b - a, c - a);
                float mag = cross.magnitude;
                data[f].normal = mag > 1e-12f ? cross / mag : Vector3.up;
                data[f].area   = mag * 0.5f;
            }
            return data;
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

        static Shell3D[] ExtractShells(Face3D[] faces, int[] tris, float thresholdDeg)
        {
            var faceToShell = new int[faces.Length];
            return ExtractShells(faces, tris, thresholdDeg, faceToShell);
        }

        /// <summary>Variant that also fills <paramref name="faceToShellOut"/> with the
        /// shell index per face. The array must be pre-allocated to faces.Length.</summary>
        static Shell3D[] ExtractShells(Face3D[] faces, int[] tris, float thresholdDeg, int[] faceToShellOut)
        {
            int n = faces.Length;
            if (n == 0) return new Shell3D[0];
            // Adjacency: edge → face list. Edge key packs (min(va,vb), max).
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
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
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

        // ─── Classification (probe v3 logic, lifted) ─────────────────

        static ClassifyResult ClassifyFineFace(Face3D fine, Shell3D[] shells, Options opts, float meshDiag)
        {
            if (shells.Length == 0) return new ClassifyResult { promote = true, parentShellIdx = -1 };

            // K nearest by centroid distance.
            int K = Mathf.Min(opts.shellSearchK, shells.Length);
            var top = new (float dsq, int si)[K];
            for (int k = 0; k < K; k++) top[k] = (float.MaxValue, -1);
            for (int si = 0; si < shells.Length; si++)
            {
                float dsq = (fine.centroid - shells[si].centroid).sqrMagnitude;
                if (dsq >= top[K - 1].dsq) continue;
                int pos = K - 1;
                while (pos > 0 && top[pos - 1].dsq > dsq)
                {
                    top[pos] = top[pos - 1];
                    pos--;
                }
                top[pos] = (dsq, si);
            }

            // Among K-nearest, pick the one with smallest angle to fine.normal.
            int bestShell = -1;
            float bestAngle = float.MaxValue;
            for (int k = 0; k < K; k++)
            {
                int si = top[k].si;
                if (si < 0) break;
                float dot = Vector3.Dot(fine.normal, shells[si].dominantNormal);
                if (dot >  1f) dot =  1f;
                if (dot < -1f) dot = -1f;
                float ang = Mathf.Acos(dot) * Mathf.Rad2Deg;
                if (ang < bestAngle) { bestAngle = ang; bestShell = si; }
            }
            if (bestShell < 0) return new ClassifyResult { promote = true, parentShellIdx = -1 };

            // Three-criterion promotion check (probe v3 default thresholds).
            float ratio = shells[bestShell].totalArea > 1e-12f
                ? fine.area / shells[bestShell].totalArea
                : float.PositiveInfinity;
            float perpAbs = Mathf.Abs(Vector3.Dot(fine.centroid - shells[bestShell].centroid,
                shells[bestShell].dominantNormal));
            float perpNorm = perpAbs / meshDiag;

            bool promote = bestAngle > opts.promoteAngleDeg
                        || ratio     > opts.promoteRatio
                        || perpNorm  > opts.promotePerpNorm;
            return new ClassifyResult { promote = promote, parentShellIdx = bestShell };
        }

        // ─── Cluster promoted fine faces into mini-shells ────────────

        /// <summary>
        /// Union-find on the subset of mesh faces listed in
        /// <paramref name="faceMask"/>, with the same normal-threshold rule as
        /// <see cref="ExtractShells"/>. Returns one <see cref="PromotedCluster"/>
        /// per connected component.
        /// </summary>
        static List<PromotedCluster> ClusterFaces(Face3D[] faces, int[] tris,
            List<int> faceMask, float thresholdDeg)
        {
            int m = faceMask.Count;
            if (m == 0) return new List<PromotedCluster>();
            // Quick membership lookup.
            var inMask = new HashSet<int>(faceMask);

            // Build edge → mask-face list (only counting mask faces).
            var edgeFaces = new Dictionary<long, List<int>>(m * 3);
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
            foreach (int f in faceMask)
            {
                int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
                AddEdge(v0, v1, f); AddEdge(v1, v2, f); AddEdge(v2, v0, f);
            }

            // Union-find indexed by original face index (only mask subset is touched).
            var parent = new Dictionary<int, int>(m);
            foreach (int f in faceMask) parent[f] = f;
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
            float thresholdCos = Mathf.Cos(thresholdDeg * Mathf.Deg2Rad);
            foreach (var kv in edgeFaces)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!inMask.Contains(list[i]) || !inMask.Contains(list[j])) continue;
                        float d = Vector3.Dot(faces[list[i]].normal, faces[list[j]].normal);
                        if (d >= thresholdCos) Union(list[i], list[j]);
                    }
            }

            // Aggregate per root.
            var byRoot = new Dictionary<int, PromotedCluster>();
            foreach (int f in faceMask)
            {
                int r = Find(f);
                if (!byRoot.TryGetValue(r, out var cluster))
                {
                    cluster = new PromotedCluster();
                    byRoot[r] = cluster;
                }
                cluster.faceIndices.Add(f);
                cluster.totalArea += faces[f].area;
                cluster.centroid       += faces[f].centroid * faces[f].area;
                cluster.dominantNormal += faces[f].normal   * faces[f].area;
            }
            var result = new List<PromotedCluster>(byRoot.Count);
            foreach (var c in byRoot.Values)
            {
                if (c.totalArea > 1e-12f)
                {
                    c.centroid /= c.totalArea;
                    var nn = c.dominantNormal / c.totalArea;
                    float mn = nn.magnitude;
                    c.dominantNormal = mn > 1e-12f ? nn / mn : Vector3.up;
                }
                else { c.dominantNormal = Vector3.up; }
                ComputePlaneBasis(c.dominantNormal, out c.basisU, out c.basisV);
                ComputeExtents(faces, c.faceIndices, c.centroid, c.basisU, c.basisV,
                    out c.extentU, out c.extentV);
                result.Add(c);
            }
            return result;
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
        /// PR-2.5 swaps this for xatlas via XatlasNative. The contract is the same:
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
        static bool ValidateDryRun()
            => Selection.activeGameObject?.GetComponentInParent<LODGroup>() != null;

        [MenuItem(DryRunMenuPath)]
        static void DryRun()
        {
            var lg = Selection.activeGameObject?.GetComponentInParent<LODGroup>();
            if (lg == null)
            {
                EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                    "Select a GameObject under a LODGroup first.", "OK");
                return;
            }
            var result = Build(lg, Options.Default);
            if (!string.IsNullOrEmpty(result.error))
            {
                EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                    $"Build failed: {result.error}", "OK");
                return;
            }
            string reportPath = WriteDryRunReport(lg.name, result);
            LogDryRunSummary(lg.name, result);
            EditorUtility.DisplayDialog("Hierarchical Atlas Dry-Run",
                $"Build complete.\n\nDomains: {result.domains.Length} " +
                $"({result.baseShellCount} base + {result.promotedClusterCount} promoted)\n" +
                $"Atlas: {result.atlasPixelWidth}×{result.atlasPixelHeight}px\n" +
                $"Promotion: {result.promotedFineFaces}/{result.totalFineFaces} " +
                $"({100f * result.promotedFineFaces / Mathf.Max(1, result.totalFineFaces):F1}%)\n\n" +
                $"Report: {reportPath}\n\nSee console for details.", "OK");
        }

        static void LogDryRunSummary(string lgName, Result r)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[HierRepack] Dry-run on '{lgName}':");
            sb.AppendLine($"  domains:        {r.domains.Length} total " +
                $"({r.baseShellCount} base / {r.promotedClusterCount} promoted clusters)");
            sb.AppendLine($"  atlas:          {r.atlasPixelWidth} × {r.atlasPixelHeight} px " +
                $"(naive strip packer — PR-2.5 will swap in xatlas)");
            float promPct = 100f * r.promotedFineFaces / Mathf.Max(1, r.totalFineFaces);
            sb.AppendLine($"  fine faces:     {r.totalFineFaces} total, " +
                $"{r.promotedFineFaces} promoted ({promPct:F1}%)");

            // Per-LOD assignment audit — how many of each LOD's faces got placed
            // into base shells vs promoted clusters vs unassigned.
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
                sb.AppendLine($"    LOD{li}: {arr.Length,5} faces  " +
                    $"base={baseCount,5}  promoted={promCount,5}  unassigned={miss}");
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
