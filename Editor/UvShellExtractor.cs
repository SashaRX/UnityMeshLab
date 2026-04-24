// UvShellExtractor.cs — Shell extraction + per-face material ID for overlap separation
// Place in Assets/Editor/

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Geometry-based descriptor for a UV shell. Computed from UV0 properties
    /// that are invariant to vertex reordering, providing a stable identity
    /// for shells across reimports.
    /// </summary>
    [Serializable]
    public struct ShellDescriptor
    {
        /// <summary>Signed area of the shell in UV0 space.</summary>
        public float uv0Area;
        /// <summary>Centroid of the shell in UV0 space.</summary>
        public Vector2 uv0Centroid;
        /// <summary>Total boundary edge length in UV0 space.</summary>
        public float boundaryLength;
        /// <summary>Number of faces in the shell.</summary>
        public int faceCount;
        /// <summary>Stable hash computed from the above fields.</summary>
        public int stableHash;

        /// <summary>
        /// Compute a stable hash from geometry-based fields.
        /// Quantizes floats to avoid floating-point noise across reimports.
        /// </summary>
        public static int ComputeHash(float uv0Area, Vector2 uv0Centroid, float boundaryLength, int faceCount)
        {
            unchecked
            {
                // Quantize to 4 decimal places — enough precision, stable across reimport
                int qArea = Mathf.RoundToInt(uv0Area * 10000f);
                int qCx   = Mathf.RoundToInt(uv0Centroid.x * 10000f);
                int qCy   = Mathf.RoundToInt(uv0Centroid.y * 10000f);
                int qBnd  = Mathf.RoundToInt(boundaryLength * 10000f);

                // FNV-1a style hash
                uint h = 2166136261u;
                h = (h ^ (uint)qArea) * 16777619u;
                h = (h ^ (uint)qCx)   * 16777619u;
                h = (h ^ (uint)qCy)   * 16777619u;
                h = (h ^ (uint)qBnd)  * 16777619u;
                h = (h ^ (uint)faceCount) * 16777619u;
                return (int)h;
            }
        }

        /// <summary>Compute descriptor from extracted shell data and UV array.</summary>
        public static ShellDescriptor Compute(UvShell shell, Vector2[] uvs, int[] triangles)
        {
            var desc = new ShellDescriptor();
            desc.faceCount = shell.faceIndices.Count;

            // Signed area and centroid in UV0
            float area = 0f;
            Vector2 centroid = Vector2.zero;
            int vertCount = 0;
            foreach (int fi in shell.faceIndices)
            {
                int i0 = triangles[fi * 3], i1 = triangles[fi * 3 + 1], i2 = triangles[fi * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                Vector2 a = uvs[i0], b = uvs[i1], c = uvs[i2];
                area += (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            desc.uv0Area = area * 0.5f;

            // Centroid from unique vertices
            foreach (int v in shell.vertexIndices)
            {
                if (v >= uvs.Length) continue;
                centroid += uvs[v];
                vertCount++;
            }
            if (vertCount > 0) centroid /= vertCount;
            desc.uv0Centroid = centroid;

            // Boundary length — sum of edges that appear only once
            var edgeCounts = new Dictionary<ulong, int>();
            var edgeUvs = new Dictionary<ulong, (int a, int b)>();
            foreach (int fi in shell.faceIndices)
            {
                int i0 = triangles[fi * 3], i1 = triangles[fi * 3 + 1], i2 = triangles[fi * 3 + 2];
                AddEdgeForBoundary(i0, i1, edgeCounts, edgeUvs);
                AddEdgeForBoundary(i1, i2, edgeCounts, edgeUvs);
                AddEdgeForBoundary(i2, i0, edgeCounts, edgeUvs);
            }
            float bndLen = 0f;
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1)
                {
                    var e = edgeUvs[kv.Key];
                    if (e.a < uvs.Length && e.b < uvs.Length)
                        bndLen += Vector2.Distance(uvs[e.a], uvs[e.b]);
                }
            }
            desc.boundaryLength = bndLen;

            desc.stableHash = ComputeHash(desc.uv0Area, desc.uv0Centroid, desc.boundaryLength, desc.faceCount);
            return desc;
        }

        static void AddEdgeForBoundary(int a, int b, Dictionary<ulong, int> counts, Dictionary<ulong, (int, int)> uvs)
        {
            if (a == b) return;
            int lo = a < b ? a : b, hi = a < b ? b : a;
            ulong key = ((ulong)(uint)lo << 32) | (uint)hi;
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
            if (!uvs.ContainsKey(key)) uvs[key] = (a, b);
        }
    }

    public class UvShell
    {
        public int shellId;
        public List<int> faceIndices = new List<int>();
        public HashSet<int> vertexIndices = new HashSet<int>();
        public Vector2 boundsMin;
        public Vector2 boundsMax;
        public float bboxArea;
        /// <summary>Stable geometry-based descriptor. Computed by UvShellExtractor.Extract when computeDescriptors=true.</summary>
        public ShellDescriptor descriptor;
        /// <summary>Whether descriptor has been computed.</summary>
        public bool hasDescriptor;
        /// <summary>Symmetry split axis (0=X, 1=Y, 2=Z). -1 means not a SymSplit product.</summary>
        public int symSplitAxis = -1;
        /// <summary>Symmetry split side: +1 = positive side (groupA), -1 = negative side (groupB), 0 = not split.</summary>
        public int symSplitSide = 0;
    }

    public static class UvShellExtractor
    {
        /// <summary>
        /// Extract UV shells via Union-Find on faces by shared vertex index.
        /// In Unity, UV seams = duplicated vertices, so connected components
        /// by shared vertex index = UV shells.
        /// </summary>
        public static List<UvShell> Extract(Vector2[] uvs, int[] triangles) => Extract(uvs, triangles, false);

        /// <summary>
        /// Extract UV shells via Union-Find. When computeDescriptors=true,
        /// each shell gets a stable ShellDescriptor based on UV0 geometry.
        /// </summary>
        public static List<UvShell> Extract(Vector2[] uvs, int[] triangles, bool computeDescriptors)
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

            if (computeDescriptors)
            {
                for (int i = 0; i < shells.Count; i++)
                {
                    shells[i].descriptor = ShellDescriptor.Compute(shells[i], uvs, triangles);
                    shells[i].hasDescriptor = true;
                }
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

        /// <summary>
        /// Count the total number of shell pairs whose UV0 bounding boxes overlap
        /// above the given threshold. Useful as a metric for auto-tuning parameters.
        /// </summary>
        public static int CountAabbOverlaps(List<UvShell> shells, float threshold = 0.25f)
        {
            int count = 0;
            for (int i = 0; i < shells.Count; i++)
                for (int j = i + 1; j < shells.Count; j++)
                    if (BboxOverlapRatio(shells[i], shells[j]) > threshold)
                        count++;
            return count;
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
