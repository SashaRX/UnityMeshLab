// BorderPrimitiveDetector.cs — Finds UV border edges and border primitives
// A border edge has no UV-neighbor on one side within the same shell,
// or its neighbor belongs to a different shell.
// A border primitive is any triangle that contains a border edge.

using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public static class BorderPrimitiveDetector
    {
        /// <summary>
        /// Find border edges and border primitives for a given UV channel.
        /// Edge is "border" if:
        ///   - only one triangle uses it (mesh boundary), or
        ///   - the two triangles sharing it belong to different shells
        /// </summary>
        public static void Detect(
            int[] triangles,
            int faceCount,
            int[] triangleToShellId,
            out HashSet<long> borderEdges,
            out HashSet<int> borderPrimitiveIds)
        {
            borderEdges = new HashSet<long>();
            borderPrimitiveIds = new HashSet<int>();

            // Build edge → face list
            // Edge key = packed pair of vertex indices (smaller first)
            var edgeToFaces = new Dictionary<long, List<int>>();

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = triangles[f * 3];
                int i1 = triangles[f * 3 + 1];
                int i2 = triangles[f * 3 + 2];

                AddEdge(edgeToFaces, i0, i1, f);
                AddEdge(edgeToFaces, i1, i2, f);
                AddEdge(edgeToFaces, i2, i0, f);
            }

            // Classify edges
            foreach (var kv in edgeToFaces)
            {
                long edgeKey = kv.Key;
                var faces = kv.Value;

                bool isBorder = false;

                if (faces.Count == 1)
                {
                    // Mesh boundary edge
                    isBorder = true;
                }
                else
                {
                    // Check if all faces sharing this edge belong to the same shell
                    int shell0 = triangleToShellId[faces[0]];
                    for (int i = 1; i < faces.Count; i++)
                    {
                        if (triangleToShellId[faces[i]] != shell0)
                        {
                            isBorder = true;
                            break;
                        }
                    }
                }

                if (isBorder)
                {
                    borderEdges.Add(edgeKey);
                    foreach (int f in faces)
                        borderPrimitiveIds.Add(f);
                }
            }
        }

        /// <summary>
        /// Detect border on target mesh after initial transfer,
        /// using provisional UV to build connectivity.
        /// Uses UV-space connectivity: edge is border if UV coords differ
        /// across shared topological edge.
        /// </summary>
        public static void DetectByUvConnectivity(
            Vector2[] uvs,
            int[] triangles,
            int faceCount,
            out HashSet<long> borderEdges,
            out HashSet<int> borderPrimitiveIds)
        {
            borderEdges = new HashSet<long>();
            borderPrimitiveIds = new HashSet<int>();

            // Build topological edge → face list using vertex indices
            var edgeToFaces = new Dictionary<long, List<int>>();

            for (int f = 0; f < faceCount; f++)
            {
                int i0 = triangles[f * 3];
                int i1 = triangles[f * 3 + 1];
                int i2 = triangles[f * 3 + 2];

                AddEdge(edgeToFaces, i0, i1, f);
                AddEdge(edgeToFaces, i1, i2, f);
                AddEdge(edgeToFaces, i2, i0, f);
            }

            // An edge is UV-border if:
            // - only one face (mesh boundary)
            // - two faces share the edge topologically but their UV values
            //   at the shared vertices are different (UV seam)
            const float UV_EPS = 1e-5f;

            foreach (var kv in edgeToFaces)
            {
                long edgeKey = kv.Key;
                var faces = kv.Value;

                bool isBorder = false;

                if (faces.Count == 1)
                {
                    isBorder = true;
                }
                else if (faces.Count == 2)
                {
                    // In Unity's vertex model, shared vertices have same UV
                    // so if vertex indices are shared, UV is shared.
                    // But after transfer, two faces might share a vertex index
                    // yet we need to check UV continuity.
                    // Since Unity doesn't allow per-face UV, shared vertex = shared UV.
                    // Border detection here is by shell membership of the provisional UV.
                    // We use the simpler approach: build shells from UV, then detect by shell diff.
                    isBorder = false; // handled below
                }
                else
                {
                    // Non-manifold edge
                    isBorder = true;
                }

                if (isBorder)
                {
                    borderEdges.Add(edgeKey);
                    foreach (int f in faces)
                        borderPrimitiveIds.Add(f);
                }
            }

            // Additionally: build UV shells on provisional UV and detect
            // edges where neighboring faces are in different shells
            var shells = UvShellExtractor.Extract(uvs, triangles);
            int[] faceToShell = new int[faceCount];
            foreach (var shell in shells)
                foreach (int f in shell.faceIndices)
                    faceToShell[f] = shell.shellId;

            foreach (var kv in edgeToFaces)
            {
                if (borderEdges.Contains(kv.Key)) continue;

                var faces = kv.Value;
                if (faces.Count < 2) continue;

                int s0 = faceToShell[faces[0]];
                for (int i = 1; i < faces.Count; i++)
                {
                    if (faceToShell[faces[i]] != s0)
                    {
                        borderEdges.Add(kv.Key);
                        foreach (int f in faces)
                            borderPrimitiveIds.Add(f);
                        break;
                    }
                }
            }
        }

        // ─── Helpers ───

        static void AddEdge(Dictionary<long, List<int>> dict, int v0, int v1, int face)
        {
            long key = PackEdge(v0, v1);
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                dict[key] = list;
            }
            list.Add(face);
        }

        public static long PackEdge(int v0, int v1)
        {
            if (v0 > v1) { int t = v0; v0 = v1; v1 = t; }
            return ((long)v0 << 32) | (uint)v1;
        }
    }
}
