// ShellUnfoldRepair.cs — repair a deepest-LOD lightmap unwrap by un-folding
// symmetric / overlapping UV islands in place.
//
// The authored UV0 of packed game assets folds symmetric / repeated geometry
// onto shared UV space to save texture area: copies overlap, and mirrored
// copies carry inverted winding. A lightmap needs the opposite — every surface
// patch its own non-overlapping, correctly-wound texels. This module un-folds
// that packing: detect the overlaps + inversions, mark the symmetry seam (the
// shared 3D edge), reflect the inverted copy back over the seam, re-stitch
// across it, then optionally relax. Preserve-first — rigid UV transforms only
// (rotate / mirror / translate) until the ARAP relax pass; authored chart
// shapes are kept.
//
// Commit 1/N — DETECT + SEAM-MARK only. Analyze() is pure diagnostics and never
// mutates the mesh. Un-fold (binary, N-fold), relocate, pack and relax follow.

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class ShellUnfoldRepair
    {
        public struct Options
        {
            /// <summary>Position-weld epsilon as a fraction of the mesh bbox
            /// diagonal — collapses UV-seam-duplicated vertices so 3D adjacency
            /// (the symmetry seam) is found across UV cuts.</summary>
            public float weldEpsFrac;
            /// <summary>Skip UV triangles below this |area| in the overlap test
            /// (slivers can't carry a meaningful overlap and cost SAT time).</summary>
            public float minTriUvArea;
            public static Options Default => new Options { weldEpsFrac = 1e-5f, minTriUvArea = 1e-12f };
        }

        /// <summary>One UV island — a connected component of mesh.uv by shared
        /// vertex index (so it never overlaps itself; only other islands).</summary>
        public class Island
        {
            public int id;
            public List<int> faces;                     // triangle indices (tris/3)
            public HashSet<int> verts = new HashSet<int>();
            public float signedUvArea;                  // < 0  =>  inverted (mirror)
            public Vector2 uvMin, uvMax;
        }

        /// <summary>A marked symmetry seam: a shared 3D edge between two
        /// overlapping islands (position-welded vertex ids). The un-fold step
        /// reflects the inverted island over this seam's UV image; re-stitch
        /// welds across it. Carried to finer LODs with the per-island transform
        /// during the cascade.</summary>
        public struct SeamEdge
        {
            public int wa, wb;          // welded vertex ids of the shared 3D edge
            public int islandA, islandB;
        }

        public class Report
        {
            public int islandCount;
            public int invertedCount;
            public int overlapPairs;
            public int seamCount;       // overlapping pairs that share a 3D edge
            public List<SeamEdge> seams = new List<SeamEdge>();
            public List<Island> islands = new List<Island>();
            public string Summary =>
                $"islands={islandCount} inverted={invertedCount} " +
                $"overlapPairs={overlapPairs} seams={seamCount}";
        }

        /// <summary>Detect overlaps + inversions on mesh.uv (UVSet0) and mark the
        /// symmetry seams (shared 3D edges of overlapping islands). Pure
        /// read-only diagnostics — the un-fold / restitch / relax follow-ups
        /// consume this Report. Returns null if the mesh has no usable UV0.</summary>
        public static Report Analyze(Mesh mesh, Options o)
        {
            if (mesh == null) return null;
            var tris = mesh.triangles;
            var uv = mesh.uv;
            var pos = mesh.vertices;
            if (uv == null || uv.Length == 0 || tris.Length < 3) return null;

            // 1. Islands from UV connectivity (reuse the shared extractor).
            var shells = UvShellExtractor.Extract(uv, tris);
            var report = new Report { islandCount = shells.Count };
            foreach (var sh in shells)
            {
                var isl = new Island { id = sh.shellId, faces = sh.faceIndices };
                float area = 0f;
                var mn = new Vector2(float.MaxValue, float.MaxValue);
                var mx = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in sh.faceIndices)
                {
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
                    area += SignedArea(uv[a], uv[b], uv[c]);
                    isl.verts.Add(a); isl.verts.Add(b); isl.verts.Add(c);
                    mn = Vector2.Min(mn, uv[a]); mx = Vector2.Max(mx, uv[a]);
                    mn = Vector2.Min(mn, uv[b]); mx = Vector2.Max(mx, uv[b]);
                    mn = Vector2.Min(mn, uv[c]); mx = Vector2.Max(mx, uv[c]);
                }
                isl.signedUvArea = area;
                isl.uvMin = mn; isl.uvMax = mx;
                if (area < 0f) report.invertedCount++;
                report.islands.Add(isl);
            }

            // 2. Pairwise UV overlap (AABB prefilter -> SAT tri-tri).
            var isls = report.islands;
            var overlapping = new List<(int, int)>();
            for (int i = 0; i < isls.Count; i++)
                for (int j = i + 1; j < isls.Count; j++)
                {
                    if (!AabbOverlap(isls[i], isls[j])) continue;
                    if (IslandsOverlap(isls[i], isls[j], tris, uv, o.minTriUvArea))
                        overlapping.Add((i, j));
                }
            report.overlapPairs = overlapping.Count;

            // 3. Position-weld -> shared 3D edges -> mark seams for overlapping
            //    pairs that are actually adjacent in 3D (the symmetry fold line).
            int[] weld = WeldPositions(pos, o.weldEpsFrac);
            var edgeToIslands = BuildEdgeIslandMap(tris, weld, isls);
            foreach (var (i, j) in overlapping)
            {
                bool marked = false;
                foreach (var kv in edgeToIslands)
                {
                    if (kv.Value.Contains(i) && kv.Value.Contains(j))
                    {
                        report.seams.Add(new SeamEdge
                        {
                            wa = (int)(kv.Key >> 32),
                            wb = (int)(kv.Key & 0xffffffffUL),
                            islandA = i,
                            islandB = j,
                        });
                        marked = true;
                    }
                }
                if (marked) report.seamCount++;
            }

            UvtLog.Info(UvtLog.Category.Repack,
                $"[UnfoldRepair] '{mesh.name}': {report.Summary}");
            return report;
        }

        // ─── geometry helpers ───────────────────────────────────────────
        static float SignedArea(Vector2 a, Vector2 b, Vector2 c)
            => 0.5f * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        static bool AabbOverlap(Island a, Island b)
            => a.uvMin.x < b.uvMax.x && a.uvMax.x > b.uvMin.x
            && a.uvMin.y < b.uvMax.y && a.uvMax.y > b.uvMin.y;

        static bool IslandsOverlap(Island A, Island B, int[] tris, Vector2[] uv, float minArea)
        {
            foreach (int fa in A.faces)
            {
                int a0 = tris[fa * 3], a1 = tris[fa * 3 + 1], a2 = tris[fa * 3 + 2];
                if (a0 >= uv.Length || a1 >= uv.Length || a2 >= uv.Length) continue;
                Vector2 p0 = uv[a0], p1 = uv[a1], p2 = uv[a2];
                if (Mathf.Abs(SignedArea(p0, p1, p2)) < minArea) continue;
                foreach (int fb in B.faces)
                {
                    int b0 = tris[fb * 3], b1 = tris[fb * 3 + 1], b2 = tris[fb * 3 + 2];
                    if (b0 >= uv.Length || b1 >= uv.Length || b2 >= uv.Length) continue;
                    Vector2 q0 = uv[b0], q1 = uv[b1], q2 = uv[b2];
                    if (Mathf.Abs(SignedArea(q0, q1, q2)) < minArea) continue;
                    if (TriOverlap(p0, p1, p2, q0, q1, q2)) return true;
                }
            }
            return false;
        }

        static bool TriOverlap(Vector2 a0, Vector2 a1, Vector2 a2, Vector2 b0, Vector2 b1, Vector2 b2)
        {
            if (AxisSeparates(Perp(a1 - a0), a0, a1, a2, b0, b1, b2)) return false;
            if (AxisSeparates(Perp(a2 - a1), a0, a1, a2, b0, b1, b2)) return false;
            if (AxisSeparates(Perp(a0 - a2), a0, a1, a2, b0, b1, b2)) return false;
            if (AxisSeparates(Perp(b1 - b0), a0, a1, a2, b0, b1, b2)) return false;
            if (AxisSeparates(Perp(b2 - b1), a0, a1, a2, b0, b1, b2)) return false;
            if (AxisSeparates(Perp(b0 - b2), a0, a1, a2, b0, b1, b2)) return false;
            return true;
        }
        static Vector2 Perp(Vector2 v) => new Vector2(-v.y, v.x);
        static bool AxisSeparates(Vector2 ax, Vector2 a0, Vector2 a1, Vector2 a2,
                                              Vector2 b0, Vector2 b1, Vector2 b2)
        {
            if (ax.sqrMagnitude < 1e-18f) return false;
            float a0p = Vector2.Dot(ax, a0), a1p = Vector2.Dot(ax, a1), a2p = Vector2.Dot(ax, a2);
            float b0p = Vector2.Dot(ax, b0), b1p = Vector2.Dot(ax, b1), b2p = Vector2.Dot(ax, b2);
            float amin = Mathf.Min(a0p, Mathf.Min(a1p, a2p)), amax = Mathf.Max(a0p, Mathf.Max(a1p, a2p));
            float bmin = Mathf.Min(b0p, Mathf.Min(b1p, b2p)), bmax = Mathf.Max(b0p, Mathf.Max(b1p, b2p));
            return amax <= bmin || bmax <= amin;
        }

        // ─── 3D adjacency (the seam) ─────────────────────────────────────
        static int[] WeldPositions(Vector3[] pos, float epsFrac)
        {
            int n = pos.Length;
            var mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++) { mn = Vector3.Min(mn, pos[i]); mx = Vector3.Max(mx, pos[i]); }
            float eps = Mathf.Max((mx - mn).magnitude * epsFrac, 1e-9f);
            float inv = 1f / eps;
            var map = new Dictionary<(long, long, long), int>(n);
            var weld = new int[n];
            for (int i = 0; i < n; i++)
            {
                var key = ((long)Mathf.RoundToInt(pos[i].x * inv),
                           (long)Mathf.RoundToInt(pos[i].y * inv),
                           (long)Mathf.RoundToInt(pos[i].z * inv));
                if (map.TryGetValue(key, out int first)) weld[i] = first;
                else { map[key] = i; weld[i] = i; }
            }
            return weld;
        }

        static Dictionary<ulong, HashSet<int>> BuildEdgeIslandMap(int[] tris, int[] weld, List<Island> isls)
        {
            var faceIsl = new Dictionary<int, int>();
            for (int k = 0; k < isls.Count; k++)
                foreach (int f in isls[k].faces) faceIsl[f] = k;
            var map = new Dictionary<ulong, HashSet<int>>();
            int faceCount = tris.Length / 3;
            for (int f = 0; f < faceCount; f++)
            {
                if (!faceIsl.TryGetValue(f, out int isl)) continue;
                int a = weld[tris[f * 3]], b = weld[tris[f * 3 + 1]], c = weld[tris[f * 3 + 2]];
                AddEdge(map, a, b, isl); AddEdge(map, b, c, isl); AddEdge(map, c, a, isl);
            }
            return map;
        }
        static void AddEdge(Dictionary<ulong, HashSet<int>> map, int a, int b, int isl)
        {
            if (a == b) return;
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;
            if (!map.TryGetValue(key, out var set)) { set = new HashSet<int>(); map[key] = set; }
            set.Add(isl);
        }

        // ─── diagnostic entry point ──────────────────────────────────────
        [UnityEditor.MenuItem("Tools/UnityMeshLab/Diagnostics/Analyze Unfold (deepest LOD)")]
        static void AnalyzeSelectedDeepest()
        {
            var go = UnityEditor.Selection.activeGameObject;
            if (go == null)
            {
                UvtLog.Warn(UvtLog.Category.Repack, "[UnfoldRepair] no GameObject selected");
                return;
            }
            var mesh = ResolveDeepestMesh(go);
            if (mesh == null)
            {
                UvtLog.Warn(UvtLog.Category.Repack,
                    $"[UnfoldRepair] '{go.name}': no LODGroup/MeshFilter mesh found");
                return;
            }
            var rep = Analyze(mesh, Options.Default);
            if (rep == null) return;
            foreach (var s in rep.seams)
                UvtLog.Info(UvtLog.Category.Repack,
                    $"[UnfoldRepair]   seam isl{s.islandA}<->isl{s.islandB} weldedEdge({s.wa},{s.wb})");
        }

        /// <summary>Resolve the deepest-LOD mesh: last non-empty LOD of a
        /// LODGroup on (or under) <paramref name="go"/>, else its own
        /// MeshFilter. Mirrors the deepest-first convention of the cascade.</summary>
        static Mesh ResolveDeepestMesh(GameObject go)
        {
            var lg = go.GetComponent<LODGroup>();
            if (lg == null) lg = go.GetComponentInChildren<LODGroup>();
            if (lg != null)
            {
                var lods = lg.GetLODs();
                for (int li = lods.Length - 1; li >= 0; li--)
                {
                    var rs = lods[li].renderers;
                    if (rs == null) continue;
                    foreach (var r in rs)
                    {
                        if (r == null) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
                    }
                }
            }
            var own = go.GetComponent<MeshFilter>();
            return own != null ? own.sharedMesh : null;
        }
    }
}
