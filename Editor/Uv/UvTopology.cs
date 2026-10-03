// UvTopology.cs — index-space topology of a UV layout: reading a channel, the
// boundary edges of a triangle set, unique edges for a wire, point-in-triangle, the
// per-face shell map and the shell data the canvas and the 3D layer draw from. The
// canvas, the 3D layer, the shell extractor and the viewport each carried a copy of
// one or more of these; this is the one implementation. Nothing here caches: the
// framework owns the caches (per view, per context) and calls in.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class UvTopology
    {
        /// <summary>UV channel <paramref name="channel"/> of the mesh as an array, or null when the mesh has none.</summary>
        internal static Vector2[] ReadUv(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel > 7) return null;
            var list = new List<Vector2>();
            mesh.GetUVs(channel, list);
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>True when the mesh carries any data in UV channel <paramref name="channel"/>.</summary>
        internal static bool HasUv(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel > 7) return false;
            return mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel);
        }

        // ── edges ──

        // Weld only vertices agreeing in BOTH position and the displayed UV channel.
        // Normal/crease splits with continuous UVs are not UV shell boundaries.
        internal static int[] UvBoundaryEdgePairs(Mesh mesh, int channel)
        {
            var uv = ReadUv(mesh, channel);
            return UvBoundaryEdgePairs(mesh.vertices, uv, mesh.triangles);
        }

        // Array-only overload for background preview preparation. Inputs remain immutable.
        internal static int[] UvBoundaryEdgePairs(Vector3[] vertices, Vector2[] uv, int[] sourceTriangles)
        {
            if (uv == null || vertices == null || uv.Length != vertices.Length || sourceTriangles == null) return Array.Empty<int>();
            var positions = MeshGeometry.WeldPositions(vertices, out _);
            var slots = new Dictionary<(int, float, float), int>();
            var representatives = new List<int>();
            var remap = new int[uv.Length];
            for (int i = 0; i < uv.Length; ++i) {
                var key = (positions[i], uv[i].x, uv[i].y);
                if (!slots.TryGetValue(key, out int slot)) {
                    slot = slots.Count; slots[key] = slot; representatives.Add(i);
                }
                remap[i] = slot;
            }
            var triangles = new int[sourceTriangles.Length];
            for (int i = 0; i < triangles.Length; ++i) {
                int index = sourceTriangles[i];
                if (index < 0 || index >= remap.Length) return Array.Empty<int>();
                triangles[i] = remap[index];
            }
            var pairs = BoundaryEdgePairs(triangles);
            for (int i = 0; i < pairs.Length; ++i) pairs[i] = representatives[pairs[i]];
            return pairs;
        }

        static ulong EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((ulong)(uint)lo << 32) | (uint)hi;
        }

        /// <summary>
        /// The edges used by exactly one triangle of <paramref name="triangles"/> (or of
        /// the faces in <paramref name="faces"/> when given), as index pairs in the
        /// winding of the face that owns them: `[a0, b0, a1, b1, …]`. Degenerate edges
        /// (a == b) are ignored. Index-space topology: two vertices at the same position
        /// but different indices are different vertices, which is what makes this the UV
        /// island boundary on a mesh split along its seams.
        /// </summary>
        internal static int[] BoundaryEdgePairs(int[] triangles, IList<int> faces = null)
        {
            if (triangles == null || triangles.Length < 3) return Array.Empty<int>();
            int faceCount = faces != null ? faces.Count : triangles.Length / 3;
            var counts = new Dictionary<ulong, int>(faceCount * 3);
            var orient = new Dictionary<ulong, (int a, int b)>(faceCount * 3);
            for (int k = 0; k < faceCount; k++)
            {
                int f = faces != null ? faces[k] : k;
                int t0 = f * 3;
                if (t0 < 0 || t0 + 2 >= triangles.Length) continue;
                AddEdge(triangles[t0], triangles[t0 + 1], counts, orient);
                AddEdge(triangles[t0 + 1], triangles[t0 + 2], counts, orient);
                AddEdge(triangles[t0 + 2], triangles[t0], counts, orient);
            }
            var result = new List<int>();
            foreach (var kv in counts)
            {
                if (kv.Value != 1) continue;
                var e = orient[kv.Key];
                result.Add(e.a); result.Add(e.b);
            }
            return result.ToArray();
        }

        static void AddEdge(int a, int b, Dictionary<ulong, int> counts, Dictionary<ulong, (int a, int b)> orient)
        {
            if (a == b) return;
            ulong key = EdgeKey(a, b);
            counts.TryGetValue(key, out int c); counts[key] = c + 1;
            if (!orient.ContainsKey(key)) orient[key] = (a, b);
        }

        /// <summary>
        /// The summed UV length of the boundary edges of <paramref name="faces"/> (every
        /// face when null); indices outside <paramref name="uv"/> are skipped.
        /// </summary>
        internal static float BoundaryLength(Vector2[] uv, int[] triangles, IList<int> faces = null)
        {
            if (uv == null) return 0f;
            var pairs = BoundaryEdgePairs(triangles, faces);
            float length = 0f;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                int a = pairs[i], b = pairs[i + 1];
                if (a < 0 || b < 0 || a >= uv.Length || b >= uv.Length) continue;
                length += Vector2.Distance(uv[a], uv[b]);
            }
            return length;
        }

        /// <summary>
        /// Every distinct edge of the triangle list once, as a line list `[a0, b0, a1, b1, …]`
        /// in first-seen order (a wireframe). Null above <paramref name="maxFaces"/> faces.
        /// </summary>
        internal static List<int> UniqueEdges(int[] triangles, int maxFaces = 1_000_000)
        {
            if (triangles == null) return null;
            if (triangles.Length / 3 > maxFaces) return null;
            var seen = new HashSet<ulong>();
            var indices = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3)
                for (int k = 0; k < 3; ++k)
                {
                    int a = triangles[i + k], b = triangles[i + (k + 1) % 3];
                    if (seen.Add(EdgeKey(a, b))) { indices.Add(a); indices.Add(b); }
                }
            return indices;
        }

        // ── point queries ──

        /// <summary>True when <paramref name="p"/> is inside or on triangle abc, either winding.</summary>
        internal static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            float s2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
            float s3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x);
            return !((s1 < 0f || s2 < 0f || s3 < 0f) && (s1 > 0f || s2 > 0f || s3 > 0f));
        }

        // ── shells ──

        /// <summary>
        /// The shell a face belongs to, decided by its three vertices' shell ids: the id
        /// two of them share, else the first that has one, else -1. For a per-vertex map
        /// that can disagree across a face (a transferred match).
        /// </summary>
        internal static int VoteBestShell(int[] vertToShell, int i0, int i1, int i2)
        {
            int s0 = (i0 >= 0 && i0 < vertToShell.Length) ? vertToShell[i0] : -1;
            int s1 = (i1 >= 0 && i1 < vertToShell.Length) ? vertToShell[i1] : -1;
            int s2 = (i2 >= 0 && i2 < vertToShell.Length) ? vertToShell[i2] : -1;
            if (s0 >= 0 && s0 == s1) return s0;
            if (s0 >= 0 && s0 == s2) return s0;
            if (s1 >= 0 && s1 == s2) return s1;
            if (s0 >= 0) return s0;
            if (s1 >= 0) return s1;
            return s2;
        }

        /// <summary>
        /// Shell id per face of the layout (-1 where the extraction failed or a face is in
        /// no shell).
        /// </summary>
        internal static int[] FaceToShell(Vector2[] uv, int[] triangles)
        {
            if (uv == null || triangles == null) return null;
            int faceCount = triangles.Length / 3;
            var faceToShell = new int[faceCount];
            for (int i = 0; i < faceToShell.Length; i++) faceToShell[i] = -1;
            try
            {
                var shells = UvShellExtractor.Extract(uv, triangles);
                foreach (var shell in shells)
                {
                    if (shell?.faceIndices == null) continue;
                    foreach (int f in shell.faceIndices)
                        if (f >= 0 && f < faceToShell.Length) faceToShell[f] = shell.shellId;
                }
            }
            catch
            {
                // Keep the -1 mapping when shell extraction fails.
            }
            return faceToShell;
        }

        /// <summary>
        /// The shells of a layout with what the views need to draw and pick them: the
        /// face → shell and id → shell maps and each shell's UV bounds (in shell order).
        /// Null when the layout has no triangles or the extraction fails.
        /// </summary>
        internal static PreviewShellData BuildShellData(Vector2[] uv, int[] triangles)
        {
            if (uv == null || triangles == null || triangles.Length < 3) return null;

            List<UvShell> shells;
            try { shells = UvShellExtractor.Extract(uv, triangles, computeDescriptors: true); }
            catch { return null; }

            var faceToShell = new Dictionary<int, int>(triangles.Length / 3);
            var shellById = new Dictionary<int, UvShell>(shells.Count);
            var bounds = new Bounds[shells.Count];
            for (int i = 0; i < shells.Count; i++)
            {
                var shell = shells[i];
                shellById[shell.shellId] = shell;
                bool hasPoint = false;
                var b = new Bounds(Vector3.zero, Vector3.zero);
                foreach (int fi in shell.faceIndices)
                {
                    faceToShell[fi] = shell.shellId;
                    int t0 = fi * 3;
                    if (t0 + 2 >= triangles.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = triangles[t0 + k];
                        if (vi < 0 || vi >= uv.Length) continue;
                        Vector3 p = uv[vi];
                        if (!hasPoint) { b = new Bounds(p, Vector3.zero); hasPoint = true; }
                        else b.Encapsulate(p);
                    }
                }
                bounds[i] = b;
            }
            return new PreviewShellData { shells = shells, faceToShell = faceToShell, shellById = shellById, shellBounds = bounds, triangles = triangles, uvs = uv };
        }

        /// <summary>The UDIM tiles (floor of u, floor of v) the finite UVs in <paramref name="uv"/> fall into, added to <paramref name="into"/>.</summary>
        internal static void OccupiedTiles(Vector2[] uv, HashSet<Vector2Int> into, Func<Vector2, bool> accept = null)
        {
            if (uv == null || into == null) return;
            for (int i = 0; i < uv.Length; i++)
            {
                var u = uv[i];
                if (float.IsNaN(u.x) || float.IsNaN(u.y) || float.IsInfinity(u.x) || float.IsInfinity(u.y)) continue;
                if (accept != null && !accept(u)) continue;
                into.Add(new Vector2Int(Mathf.FloorToInt(u.x), Mathf.FloorToInt(u.y)));
            }
        }
    }
}
