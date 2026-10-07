using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Final shading normals on an already unwrapped mesh. UVs and charts stay fixed.</summary>
    internal static class RemeshNormals
    {
        struct Edge
        {
            internal int first, second, count;
        }

        internal static void ApplyFinal(RemeshNative.Geometry geometry, RemeshSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (geometry.draftUv) throw new InvalidOperationException("Final normals require an unwrapped atlas; UV0 is still draft.");
            if (geometry.normals == null || geometry.tangents == null || geometry.uv == null || geometry.charts == null)
                throw new InvalidOperationException("Final normals require the unwrap's normals, tangents, UV0 and chart ids.");
            bool islands = settings.hardEdges == RemeshHardEdges.UvIslands || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle;
            bool angle = (settings.hardEdges == RemeshHardEdges.Angle || settings.hardEdges == RemeshHardEdges.UvIslandsAndAngle) && settings.normalCrease < 180;
            int[] groups;
            if (angle) {
                groups = SplitCreases(geometry, settings.normalCrease, islands, token);
                RemeshNative.GenerateGroupedNormals(geometry, settings.normalWeighting, groups);
            }
            else {
                var nativeNormals = (Vector3[])geometry.normals.Clone();
                groups = RemeshNative.GenerateSplitNormals(geometry, settings.normalWeighting, islands ? null : nativeNormals);
            }
            geometry.normals = MeshGeometry.NormalsOrFallback(geometry.positions, geometry.indices, geometry.normals, token);
            RemeshNative.SmoothNormals(geometry, settings.normalSmoothing, groups);
            geometry.normals = MeshGeometry.NormalsOrFallback(geometry.positions, geometry.indices, geometry.normals, token);
            RemeshNative.OrthogonalizeTangents(geometry);
        }

        // A corner fan joins across a manifold edge only when the face angle is
        // below the crease. Position welding lets Angle smooth across UV seams;
        // the combined mode also stops at chart borders. Disconnected fans and
        // non-manifold edges never join merely because positions coincide.
        static int[] SplitCreases(RemeshNative.Geometry geometry, float crease, bool islands, CancellationToken token)
        {
            var indices = geometry.indices;
            var slots = MeshGeometry.WeldPositions(geometry.positions, out _);
            var faces = MeshGeometry.FaceNormals(geometry.positions, indices);
            var parent = new int[indices.Length];
            for (int i = 0; i < parent.Length; ++i) parent[i] = i;
            var edges = new Dictionary<(int, int), Edge>();
            for (int f = 0; f < indices.Length; f += 3) {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; ++k) {
                    int corner = f + k, next = f + (k + 1) % 3;
                    int a = slots[indices[corner]], b = slots[indices[next]];
                    if (a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var edge)) edge = new Edge { first = corner, second = -1 };
                    else if (edge.count == 1) edge.second = corner;
                    ++edge.count; edges[key] = edge;
                }
            }
            float cosine = Mathf.Cos(crease * Mathf.Deg2Rad);
            int processed = 0;
            foreach (var edge in edges.Values) {
                if ((processed++ & 4095) == 0) token.ThrowIfCancellationRequested();
                if (edge.count != 2) continue;
                int a = edge.first, b = edge.second;
                var na = faces[a / 3]; var nb = faces[b / 3];
                if (na == Vector3.zero || nb == Vector3.zero || Vector3.Dot(na, nb) < cosine) continue;
                if (islands && geometry.charts[indices[a]] != geometry.charts[indices[b]]) continue;
                int an = a / 3 * 3 + (a + 1) % 3, bn = b / 3 * 3 + (b + 1) % 3;
                if (slots[indices[a]] == slots[indices[b]]) {
                    Join(parent, a, b); Join(parent, an, bn);
                }
                else { Join(parent, a, bn); Join(parent, an, b); }
            }
            // Duplicate a render vertex for each normal fan that uses it. Copy
            // every channel verbatim, so splitting a hard normal cannot move a
            // UV corner, open an atlas seam or change a tangent's handedness.
            var p = new List<Vector3>(geometry.positions);
            var n = new List<Vector3>(geometry.normals);
            var uv = new List<Vector2>(geometry.uv);
            var t = new List<Vector4>(geometry.tangents);
            var charts = new List<int>(geometry.charts);
            var groups = new List<int>(new int[p.Count]);
            for (int i = 0; i < groups.Count; ++i) groups[i] = -1;
            var compactGroups = new Dictionary<int, int>();
            var remap = new Dictionary<(int, int), int>();
            var output = new int[indices.Length];
            for (int corner = 0; corner < indices.Length; ++corner) {
                if ((corner & 4095) == 0) token.ThrowIfCancellationRequested();
                int root = Root(parent, corner);
                if (!compactGroups.TryGetValue(root, out int group)) { group = compactGroups.Count; compactGroups[root] = group; }
                int v = indices[corner];
                if (!remap.TryGetValue((v, group), out int dst)) {
                    dst = v;
                    if (groups[v] >= 0) {
                        dst = p.Count;
                        p.Add(geometry.positions[v]); n.Add(geometry.normals[v]); uv.Add(geometry.uv[v]);
                        t.Add(geometry.tangents[v]); charts.Add(geometry.charts[v]); groups.Add(group);
                    }
                    else groups[v] = group;
                    remap[(v, group)] = dst;
                }
                output[corner] = dst;
            }
            // An unused native vertex still has complete channels and its own fan.
            for (int i = 0; i < groups.Count; ++i) if (groups[i] < 0) groups[i] = compactGroups.Count + i;
            geometry.positions = p.ToArray(); geometry.normals = n.ToArray(); geometry.uv = uv.ToArray();
            geometry.tangents = t.ToArray(); geometry.charts = charts.ToArray(); geometry.indices = output;
            return groups.ToArray();
        }

        static int Root(int[] parent, int corner)
        {
            int root = corner;
            while (parent[root] != root) root = parent[root];
            while (parent[corner] != corner) { int next = parent[corner]; parent[corner] = root; corner = next; }
            return root;
        }

        static void Join(int[] parent, int a, int b)
        {
            int ra = Root(parent, a), rb = Root(parent, b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }
}
