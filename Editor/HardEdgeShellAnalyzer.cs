// HardEdgeShellAnalyzer.cs — detect shells whose 3D surface contains hard
// edges (face-normal-pair angle above a threshold) such that cutting along
// them would split the shell into multiple connected sub-components.
//
// Pure analysis. mesh.uv and the shell data structures are not mutated; the
// caller decides what to do with the result. Today this powers a logging
// diagnostic; a follow-up will use the per-face sub-component assignment to
// drive an actual pre-pack split by emitting distinct faceShellIds for each
// sub-component into xatlas's AddUvMesh call.

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class HardEdgeShellAnalyzer
    {
        public readonly struct ShellSplitInfo
        {
            public readonly int shellId;
            /// <summary>Raw Union-Find component count after cutting hard edges.</summary>
            public readonly int totalComponents;
            /// <summary>Components with at least minSubshellFaces faces — these are
            /// the ones worth materialising as separate xatlas charts.</summary>
            public readonly int eligibleComponents;
            /// <summary>Number of edge pairs (face/face adjacencies) whose normals
            /// disagreed by more than the angle threshold.</summary>
            public readonly int hardEdgeCount;
            /// <summary>Per-face sub-component index, indexed by position inside
            /// the shell's faceIndices list (NOT by global face index). Drives
            /// the future actual split.</summary>
            public readonly int[] perFaceComponent;

            public ShellSplitInfo(int id, int total, int eligible, int hard, int[] perFace)
            {
                shellId = id;
                totalComponents = total;
                eligibleComponents = eligible;
                hardEdgeCount = hard;
                perFaceComponent = perFace;
            }
        }

        public readonly struct AnalysisResult
        {
            public readonly int totalShellsAnalyzed;
            public readonly int shellsWithHardEdges;
            /// <summary>Shells whose eligibleComponents ≥ 2 — the only ones a
            /// real split would actually act on. User's rule: don't cut if the
            /// outcome is "unstitch one face from the rest"; cut only when
            /// the shell genuinely separates into multiple chunks.</summary>
            public readonly int shellsSplittable;
            public readonly List<ShellSplitInfo> splittable;

            public AnalysisResult(int total, int withHard, int splittableCount, List<ShellSplitInfo> list)
            {
                totalShellsAnalyzed = total;
                shellsWithHardEdges = withHard;
                shellsSplittable = splittableCount;
                splittable = list;
            }
        }

        /// <summary>
        /// For each shell, build face-adjacency by shared edge (2 verts), tag
        /// each adjacency as "hard" when the angle between geometric face
        /// normals exceeds <paramref name="angleThresholdDeg"/>, then run
        /// Union-Find on the soft-only adjacency. A shell is reported as
        /// splittable only when at least 2 of the resulting sub-components
        /// have ≥ <paramref name="minSubshellFaces"/> faces each — single-face
        /// splinters don't count.
        /// </summary>
        public static AnalysisResult Analyze(
            List<UvShell> shells, int[] tris, Vector3[] positions,
            float angleThresholdDeg = 45f, int minSubshellFaces = 2)
        {
            if (shells == null || tris == null || positions == null)
                return new AnalysisResult(0, 0, 0, new List<ShellSplitInfo>());

            int triCount = tris.Length / 3;
            int posLen = positions.Length;
            float cosThreshold = Mathf.Cos(angleThresholdDeg * Mathf.Deg2Rad);

            // Precompute face normals once across the whole mesh.
            var faceNormals = new Vector3[triCount];
            for (int f = 0; f < triCount; f++)
            {
                int t = f * 3;
                if ((uint)(t + 2) >= (uint)tris.Length) continue;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)posLen || (uint)i1 >= (uint)posLen || (uint)i2 >= (uint)posLen) continue;
                Vector3 cross = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                faceNormals[f] = cross.sqrMagnitude > 1e-12f ? cross.normalized : Vector3.up;
            }

            int totalAnalyzed = 0;
            int withHardEdges = 0;
            int splittableCount = 0;
            var splittable = new List<ShellSplitInfo>();
            var edgeFaces = new Dictionary<long, List<int>>(64);
            var faceIdx = new Dictionary<int, int>(64);

            foreach (var shell in shells)
            {
                if (shell?.faceIndices == null || shell.faceIndices.Count < 2) continue;
                totalAnalyzed++;

                // Build edge → faces map for this shell only.
                edgeFaces.Clear();
                foreach (int f in shell.faceIndices)
                {
                    int t = f * 3;
                    if ((uint)(t + 2) >= (uint)tris.Length) continue;
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    AddEdge(edgeFaces, i0, i1, f);
                    AddEdge(edgeFaces, i1, i2, f);
                    AddEdge(edgeFaces, i2, i0, f);
                }

                int nf = shell.faceIndices.Count;
                faceIdx.Clear();
                for (int i = 0; i < nf; i++) faceIdx[shell.faceIndices[i]] = i;

                var parent = new int[nf];
                var rankArr = new int[nf];
                for (int i = 0; i < nf; i++) parent[i] = i;

                int hardCount = 0;
                foreach (var kv in edgeFaces)
                {
                    var fs = kv.Value;
                    if (fs.Count < 2) continue;
                    int f0 = fs[0];
                    for (int j = 1; j < fs.Count; j++)
                    {
                        int fj = fs[j];
                        float dot = Vector3.Dot(faceNormals[f0], faceNormals[fj]);
                        if (dot < cosThreshold)
                        {
                            hardCount++;
                            continue;
                        }
                        if (faceIdx.TryGetValue(f0, out int a) && faceIdx.TryGetValue(fj, out int b))
                            Union(parent, rankArr, a, b);
                    }
                }
                if (hardCount > 0) withHardEdges++;

                // Component sizes (key = root index in `parent`).
                var compSize = new Dictionary<int, int>();
                for (int i = 0; i < nf; i++)
                {
                    int r = Find(parent, i);
                    if (!compSize.TryGetValue(r, out int c)) c = 0;
                    compSize[r] = c + 1;
                }

                int eligible = 0;
                foreach (var c in compSize.Values)
                    if (c >= minSubshellFaces) eligible++;

                if (eligible >= 2)
                {
                    // Materialise a stable component index per face (0..K-1)
                    // so the future actual split can read this directly.
                    var rootToIdx = new Dictionary<int, int>();
                    int next = 0;
                    var perFace = new int[nf];
                    for (int i = 0; i < nf; i++)
                    {
                        int r = Find(parent, i);
                        if (!rootToIdx.TryGetValue(r, out int idx)) { idx = next++; rootToIdx[r] = idx; }
                        perFace[i] = idx;
                    }
                    splittableCount++;
                    splittable.Add(new ShellSplitInfo(shell.shellId, compSize.Count, eligible, hardCount, perFace));
                }
            }

            return new AnalysisResult(totalAnalyzed, withHardEdges, splittableCount, splittable);
        }

        static void AddEdge(Dictionary<long, List<int>> map, int a, int b, int face)
        {
            // Canonical key: pack the smaller vert index in the high bits so
            // (a,b) and (b,a) hash the same. (long)>>32 keeps int->long sign
            // bits clean.
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                map[key] = list;
            }
            list.Add(face);
        }

        static int Find(int[] p, int i)
        {
            while (p[i] != i)
            {
                p[i] = p[p[i]];
                i = p[i];
            }
            return i;
        }

        static void Union(int[] p, int[] r, int a, int b)
        {
            int ra = Find(p, a), rb = Find(p, b);
            if (ra == rb) return;
            if (r[ra] < r[rb]) p[ra] = rb;
            else if (r[ra] > r[rb]) p[rb] = ra;
            else { p[rb] = ra; r[ra]++; }
        }
    }
}
