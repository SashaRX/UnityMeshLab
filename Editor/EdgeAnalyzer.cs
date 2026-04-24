// EdgeAnalyzer.cs — Edge-level mesh topology analysis
// Builds edge-face adjacency and classifies each geometric edge:
//   Border, Interior, UvSeam, UvFoldover, HardEdge, NonManifold.
// Foundation for hard-edge shell splitting and topology-aware UV transfer.
//
// Technique adapted from UnityMeshSimplifier's Smart Linking:
//   spatial hashing + seam/foldover distinction for split vertices.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public static class EdgeAnalyzer
    {
        // ─── Edge classification flags (combinable) ───

        [Flags]
        public enum EdgeFlag
        {
            None        = 0,
            Border      = 1 << 0,  // 1 face only (mesh boundary)
            Interior    = 1 << 1,  // 2 faces, no discontinuity
            UvSeam      = 1 << 2,  // split vertices with different UV0
            UvFoldover  = 1 << 3,  // split vertices with same UV0
            HardEdge    = 1 << 4,  // face normals differ > threshold
            NonManifold = 1 << 5   // 3+ faces share edge
        }

        // ─── Per-edge data ───

        public struct EdgeInfo
        {
            public int vertA, vertB;       // position group representatives
            public EdgeFlag flags;
            public List<int> faceIndices;  // triangles sharing this edge
        }

        // ─── Summary report ───

        public struct EdgeReport
        {
            public int totalEdges;
            public int borderEdges;
            public int interiorEdges;
            public int uvSeamEdges;
            public int uvFoldoverEdges;
            public int hardEdges;
            public int nonManifoldEdges;

            public override string ToString() =>
                $"Edges:{totalEdges} border:{borderEdges} interior:{interiorEdges} " +
                $"uvSeam:{uvSeamEdges} foldover:{uvFoldoverEdges} hard:{hardEdges} " +
                $"nonManifold:{nonManifoldEdges}";
        }

        // ═══════════════════════════════════════════════════════════
        //  Analyze: full edge classification for a mesh
        // ═══════════════════════════════════════════════════════════

        public static EdgeReport Analyze(Mesh mesh,
            out Dictionary<long, EdgeInfo> edges, float hardEdgeAngleDeg = 1f)
        {
            var verts   = mesh.vertices;
            var normals = mesh.normals;
            var uv0     = mesh.uv;
            var tris    = mesh.triangles;
            int vertCount = verts.Length;
            int triCount  = tris.Length / 3;
            bool hasNormals = normals != null && normals.Length == vertCount;
            bool hasUv0     = uv0 != null && uv0.Length == vertCount;

            float hardEdgeCos = Mathf.Cos(hardEdgeAngleDeg * Mathf.Deg2Rad);

            // ── 1. Position groups: vertices at same 3D pos → same group ID ──
            int[] posGroup = BuildPositionGroups(verts, vertCount);

            // ── 2. Build edge → face adjacency ──
            // EdgeKey = sorted pair of position group IDs
            edges = new Dictionary<long, EdgeInfo>();

            for (int f = 0; f < triCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                AddEdgeFace(edges, posGroup, i0, i1, f);
                AddEdgeFace(edges, posGroup, i1, i2, f);
                AddEdgeFace(edges, posGroup, i2, i0, f);
            }

            // ── 3. Compute face normals for hard edge detection ──
            var faceNormals = new Vector3[triCount];
            for (int f = 0; f < triCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                faceNormals[f] = Vector3.Cross(
                    verts[i1] - verts[i0],
                    verts[i2] - verts[i0]).normalized;
            }

            // ── 4. Classify each edge ──
            var report = new EdgeReport();
            // Collect updates to avoid modifying dictionary during iteration.
            var updates = new List<KeyValuePair<long, EdgeInfo>>();

            foreach (var kv in edges)
            {
                var info = kv.Value;
                int faceCount = info.faceIndices.Count;
                info.flags = EdgeFlag.None;

                if (faceCount == 1)
                {
                    info.flags |= EdgeFlag.Border;
                    report.borderEdges++;
                }
                else
                {
                    if (faceCount > 2)
                    {
                        info.flags |= EdgeFlag.NonManifold;
                        report.nonManifoldEdges++;
                    }

                    // Hard edge: check face normal angle between adjacent faces
                    bool isHard = false;
                    for (int a = 0; a < faceCount && !isHard; a++)
                    {
                        for (int b = a + 1; b < faceCount && !isHard; b++)
                        {
                            float dot = Vector3.Dot(
                                faceNormals[info.faceIndices[a]],
                                faceNormals[info.faceIndices[b]]);
                            if (dot < hardEdgeCos) isHard = true;
                        }
                    }
                    if (isHard)
                    {
                        info.flags |= EdgeFlag.HardEdge;
                        report.hardEdges++;
                    }

                    // UV seam/foldover: check if split vertices exist on this edge
                    if (hasUv0)
                    {
                        bool hasSeam, hasFoldover;
                        ClassifyEdgeUv(tris, uv0, posGroup, info.faceIndices,
                                       info.vertA, info.vertB,
                                       out hasSeam, out hasFoldover);
                        if (hasSeam)
                        {
                            info.flags |= EdgeFlag.UvSeam;
                            report.uvSeamEdges++;
                        }
                        if (hasFoldover)
                        {
                            info.flags |= EdgeFlag.UvFoldover;
                            report.uvFoldoverEdges++;
                        }
                    }

                    // Interior = multi-face, no issues
                    if ((info.flags & ~EdgeFlag.Interior) == EdgeFlag.None)
                    {
                        info.flags |= EdgeFlag.Interior;
                        report.interiorEdges++;
                    }
                }

                updates.Add(new KeyValuePair<long, EdgeInfo>(kv.Key, info));
                report.totalEdges++;
            }

            // Apply collected updates outside the iteration
            foreach (var upd in updates)
                edges[upd.Key] = upd.Value;

            return report;
        }

        // ═══════════════════════════════════════════════════════════
        //  FindHardEdgeVertices: vertices on edges where face normals
        //  differ by more than threshold. Directly useful for the
        //  planned "split UV shells along hard edges" feature.
        // ═══════════════════════════════════════════════════════════

        public static HashSet<int> FindHardEdgeVertices(
            Mesh mesh, float angleThresholdDeg = 1f)
        {
            var result = new HashSet<int>();
            Analyze(mesh, out var edges, angleThresholdDeg);

            var tris = mesh.triangles;
            var posGroup = BuildPositionGroups(mesh.vertices, mesh.vertexCount);

            foreach (var kv in edges)
            {
                if ((kv.Value.flags & EdgeFlag.HardEdge) == 0) continue;

                // Add all actual vertex indices that map to this edge's position groups
                int gA = kv.Value.vertA, gB = kv.Value.vertB;
                foreach (int f in kv.Value.faceIndices)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = tris[f * 3 + j];
                        int g = posGroup[vi];
                        if (g == gA || g == gB)
                            result.Add(vi);
                    }
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  FindBorderVertices: vertices on mesh boundary edges (1 face)
        // ═══════════════════════════════════════════════════════════

        public static HashSet<int> FindBorderVertices(Mesh mesh)
        {
            var result = new HashSet<int>();
            Analyze(mesh, out var edges);

            var tris = mesh.triangles;
            var posGroup = BuildPositionGroups(mesh.vertices, mesh.vertexCount);

            foreach (var kv in edges)
            {
                if ((kv.Value.flags & EdgeFlag.Border) == 0) continue;

                int gA = kv.Value.vertA, gB = kv.Value.vertB;
                foreach (int f in kv.Value.faceIndices)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = tris[f * 3 + j];
                        int g = posGroup[vi];
                        if (g == gA || g == gB)
                            result.Add(vi);
                    }
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  BuildPositionGroups — group vertices at same 3D position
        //  Uses spatial hashing (from UnityMeshSimplifier approach).
        //  Returns posGroup[vertexIndex] = groupId.
        // ═══════════════════════════════════════════════════════════

        public static int[] BuildPositionGroups(Vector3[] verts, int vertCount)
        {
            const float POS_EPS = 1e-6f;
            float cellSize = POS_EPS * 100f;

            // Hash → list of vertex indices in that cell
            var cells = new Dictionary<long, List<int>>();
            for (int i = 0; i < vertCount; i++)
            {
                long h = SpatialHash3D(verts[i], cellSize);
                if (!cells.TryGetValue(h, out var list))
                {
                    list = new List<int>();
                    cells[h] = list;
                }
                list.Add(i);
            }

            // Union-Find: merge vertices at same position
            int[] parent = new int[vertCount];
            int[] rank   = new int[vertCount];
            for (int i = 0; i < vertCount; i++) parent[i] = i;

            foreach (var kv in cells)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (Vec3Close(verts[list[i]], verts[list[j]], POS_EPS))
                            Union(parent, rank, list[i], list[j]);
                    }
                }
            }

            // Flatten: posGroup[i] = root representative
            int[] posGroup = new int[vertCount];
            for (int i = 0; i < vertCount; i++)
                posGroup[i] = Find(parent, i);

            return posGroup;
        }

        // ═══════════════════════════════════════════════════════════
        //  Internal helpers
        // ═══════════════════════════════════════════════════════════

        static long EdgeKey(int groupA, int groupB)
        {
            int lo = groupA < groupB ? groupA : groupB;
            int hi = groupA < groupB ? groupB : groupA;
            return ((long)lo << 32) | (uint)hi;
        }

        static void AddEdgeFace(Dictionary<long, EdgeInfo> edges,
            int[] posGroup, int v0, int v1, int faceIdx)
        {
            int gA = posGroup[v0], gB = posGroup[v1];
            if (gA == gB) return; // degenerate edge

            long key = EdgeKey(gA, gB);
            if (!edges.TryGetValue(key, out var info))
            {
                info = new EdgeInfo
                {
                    vertA = Mathf.Min(gA, gB),
                    vertB = Mathf.Max(gA, gB),
                    faceIndices = new List<int>()
                };
            }

            // Avoid duplicate face entries (each triangle adds 3 edges,
            // but the same face should only appear once per geometric edge)
            if (!info.faceIndices.Contains(faceIdx))
                info.faceIndices.Add(faceIdx);

            edges[key] = info;
        }

        /// <summary>
        /// Classify UV continuity across an edge shared by multiple faces.
        /// For each pair of faces, find the two actual vertex indices on
        /// this edge (they may differ due to UV splits) and compare UV0.
        /// </summary>
        static void ClassifyEdgeUv(int[] tris, Vector2[] uv0, int[] posGroup,
            List<int> faceIndices, int groupA, int groupB,
            out bool hasSeam, out bool hasFoldover)
        {
            hasSeam = false;
            hasFoldover = false;

            const float UV_EPS = 1e-5f;

            // Collect (vertA, vertB) per face — actual vertex indices on this edge
            var edgeVerts = new List<(int vA, int vB)>(faceIndices.Count);

            foreach (int f in faceIndices)
            {
                int vOnA = -1, vOnB = -1;
                for (int j = 0; j < 3; j++)
                {
                    int vi = tris[f * 3 + j];
                    int g = posGroup[vi];
                    if (g == groupA) vOnA = vi;
                    else if (g == groupB) vOnB = vi;
                }
                if (vOnA >= 0 && vOnB >= 0)
                    edgeVerts.Add((vOnA, vOnB));
            }

            // Compare UV0 across face pairs
            for (int a = 0; a < edgeVerts.Count; a++)
            {
                for (int b = a + 1; b < edgeVerts.Count; b++)
                {
                    bool sameA = Vec2Close(uv0[edgeVerts[a].vA], uv0[edgeVerts[b].vA], UV_EPS);
                    bool sameB = Vec2Close(uv0[edgeVerts[a].vB], uv0[edgeVerts[b].vB], UV_EPS);

                    if (sameA && sameB)
                        hasFoldover = true;  // same UV on both endpoints → foldover
                    else
                        hasSeam = true;      // UV differs → seam
                }
            }
        }

        // ─── Spatial hash ───

        static long SpatialHash3D(Vector3 v, float cellSize)
        {
            long x = (long)Mathf.FloorToInt(v.x / cellSize);
            long y = (long)Mathf.FloorToInt(v.y / cellSize);
            long z = (long)Mathf.FloorToInt(v.z / cellSize);
            return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        }

        // ─── Union-Find ───

        static int Find(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        static void Union(int[] p, int[] r, int a, int b)
        {
            a = Find(p, a); b = Find(p, b);
            if (a == b) return;
            if (r[a] < r[b]) { int t = a; a = b; b = t; }
            p[b] = a;
            if (r[a] == r[b]) r[a]++;
        }

        // ─── Comparisons ───

        static bool Vec3Close(Vector3 a, Vector3 b, float eps) =>
            Mathf.Abs(a.x - b.x) <= eps &&
            Mathf.Abs(a.y - b.y) <= eps &&
            Mathf.Abs(a.z - b.z) <= eps;

        static bool Vec2Close(Vector2 a, Vector2 b, float eps) =>
            Mathf.Abs(a.x - b.x) <= eps &&
            Mathf.Abs(a.y - b.y) <= eps;
    }
}
