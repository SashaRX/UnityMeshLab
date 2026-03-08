// UvShellExtractor.cs — Shell extraction + per-face material ID for overlap separation
// Place in Assets/Editor/

using System.Collections.Generic;
using UnityEngine;

namespace LightmapUvTool
{
    public class UvShell
    {
        public int shellId;
        public List<int> faceIndices = new List<int>();
        public HashSet<int> vertexIndices = new HashSet<int>();
        public Vector2 boundsMin;
        public Vector2 boundsMax;
        public float bboxArea;
    }

    public static class UvShellExtractor
    {
        /// <summary>
        /// Extract UV shells via Union-Find on faces by shared vertex index.
        /// In Unity, UV seams = duplicated vertices, so connected components
        /// by shared vertex index = UV shells.
        /// </summary>
        public static List<UvShell> Extract(Vector2[] uvs, int[] triangles)
        {
            int faceCount = triangles.Length / 3;
            int[] parent = new int[faceCount];
            int[] rank   = new int[faceCount];
            for (int i = 0; i < faceCount; i++) parent[i] = i;

            // vertex → faces
            var vertToFaces = new Dictionary<int, List<int>>();
            for (int f = 0; f < faceCount; f++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int v = triangles[f * 3 + j];
                    if (!vertToFaces.TryGetValue(v, out var list))
                    {
                        list = new List<int>();
                        vertToFaces[v] = list;
                    }
                    list.Add(f);
                }
            }

            // Union faces sharing a vertex
            foreach (var kv in vertToFaces)
            {
                var list = kv.Value;
                for (int i = 1; i < list.Count; i++)
                    Union(parent, rank, list[0], list[i]);
            }

            // Group faces by root
            var groups = new Dictionary<int, List<int>>();
            for (int f = 0; f < faceCount; f++)
            {
                int root = Find(parent, f);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(f);
            }

            // Build shells — sort by root face index for deterministic shell IDs
            var sortedRoots = new List<int>(groups.Keys);
            sortedRoots.Sort();
            var shells = new List<UvShell>();
            int id = 0;
            foreach (int root in sortedRoots)
            {
                var faces = groups[root];
                var shell = new UvShell { shellId = id++ };
                shell.faceIndices = faces;
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
                Vector2 mx = new Vector2(float.MinValue, float.MinValue);
                foreach (int f in faces)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int v = triangles[f * 3 + j];
                        shell.vertexIndices.Add(v);
                        mn = Vector2.Min(mn, uvs[v]);
                        mx = Vector2.Max(mx, uvs[v]);
                    }
                }
                shell.boundsMin = mn;
                shell.boundsMax = mx;
                shell.bboxArea = Mathf.Max(0f, (mx.x - mn.x) * (mx.y - mn.y));
                shells.Add(shell);
            }

            return shells;
        }

        /// <summary>
        /// Build per-face shell ID array (uint[faceCount]).
        /// Each shell gets a unique ID → pass as faceMaterialData to xatlas.
        /// xatlas never merges faces from different material IDs into one chart.
        /// This is the clean way to separate overlapping shells — no UV modification.
        /// </summary>
        public static uint[] BuildPerFaceShellIds(Vector2[] uvs, int[] triangles,
            out List<UvShell> outShells, out List<List<int>> outOverlapGroups)
        {
            outShells = Extract(uvs, triangles);
            int faceCount = triangles.Length / 3;
            uint[] ids = new uint[faceCount];

            foreach (var shell in outShells)
                foreach (int f in shell.faceIndices)
                    ids[f] = (uint)shell.shellId;

            outOverlapGroups = FindOverlapGroups(outShells);
            return ids;
        }

        /// <summary>
        /// Detect overlap groups by bounding box intersection.
        /// Used for diagnostics/reporting — the actual separation is done by faceMaterialId.
        /// </summary>
        public static List<List<int>> FindOverlapGroups(List<UvShell> shells, float threshold = 0.25f)
        {
            int n = shells.Count;
            int[] parent = new int[n];
            int[] rank   = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (BboxOverlapRatio(shells[i], shells[j]) > threshold)
                        Union(parent, rank, i, j);

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }
                list.Add(i);
            }

            var result = new List<List<int>>();
            foreach (var kv in groups)
                if (kv.Value.Count > 1)
                {
                    kv.Value.Sort(); // deterministic order by shell index
                    result.Add(kv.Value);
                }
            return result;
        }

        // ── Internals ──

        static float BboxOverlapRatio(UvShell a, UvShell b)
        {
            float oMinX = Mathf.Max(a.boundsMin.x, b.boundsMin.x);
            float oMinY = Mathf.Max(a.boundsMin.y, b.boundsMin.y);
            float oMaxX = Mathf.Min(a.boundsMax.x, b.boundsMax.x);
            float oMaxY = Mathf.Min(a.boundsMax.y, b.boundsMax.y);
            if (oMaxX <= oMinX || oMaxY <= oMinY) return 0f;
            float overlapArea = (oMaxX - oMinX) * (oMaxY - oMinY);
            float smaller = Mathf.Min(a.bboxArea, b.bboxArea);
            return smaller > 0f ? overlapArea / smaller : 0f;
        }

        static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }

        static void Union(int[] p, int[] r, int a, int b)
        {
            a = Find(p, a); b = Find(p, b);
            if (a == b) return;
            if (r[a] < r[b]) { int t = a; a = b; b = t; }
            p[b] = a;
            if (r[a] == r[b]) r[a]++;
        }
    }
}
