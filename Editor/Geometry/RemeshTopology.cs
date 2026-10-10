using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Checks geometric connectivity independently of normal/UV vertex splits.</summary>
    internal static class RemeshTopology
    {
        internal sealed class Edge
        {
            internal int count, balance, firstFace, secondFace;
        }

        internal sealed class Snapshot
        {
            internal readonly Vector3[] positions;
            internal readonly int[] indices, slots;
            internal readonly DisjointSet components;
            internal readonly Dictionary<(int, int), Edge> edges = new Dictionary<(int, int), Edge>();
            internal readonly Dictionary<(int, int, int), List<int>> faces = new Dictionary<(int, int, int), List<int>>();
            internal readonly HashSet<(Vector3, Vector3)> boundary = new HashSet<(Vector3, Vector3)>();
            internal readonly List<int> euler = new List<int>();
            internal int duplicateFaces, degenerateFaces, nonManifoldEdges, windingEdges, nonManifoldVertices;
            internal bool Valid => duplicateFaces == 0 && degenerateFaces == 0 && nonManifoldEdges == 0 && windingEdges == 0 && nonManifoldVertices == 0;
            internal string Description => $"boundary {boundary.Count}, duplicate faces {duplicateFaces}, degenerate faces {degenerateFaces}, " +
                $"non-manifold edges {nonManifoldEdges}, inconsistent edges {windingEdges}, disconnected vertex fans {nonManifoldVertices}";

            internal Snapshot(Vector3[] positions, int[] indices, CancellationToken token)
            {
                this.positions = positions; this.indices = indices;
                token.ThrowIfCancellationRequested();
                slots = MeshGeometry.WeldPositions(positions, out int vertexCount);
                var points = new Vector3[vertexCount];
                for (int i = 0; i < positions.Length; i++) points[slots[i]] = positions[i];
                components = new DisjointSet(indices.Length / 3);
                for (int f = 0; f < components.Count; f++) {
                    if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                    int a = slots[indices[f * 3]], b = slots[indices[f * 3 + 1]], c = slots[indices[f * 3 + 2]];
                    var key = FaceKey(a, b, c);
                    if (!faces.TryGetValue(key, out var list)) faces[key] = list = new List<int>(1);
                    else duplicateFaces++;
                    list.Add(f);
                    if (a == b || b == c || c == a || !MeshGeometry.HasArea(positions[indices[f * 3]], positions[indices[f * 3 + 1]], positions[indices[f * 3 + 2]]))
                        degenerateFaces++;
                    AddEdge(a, b, f); AddEdge(b, c, f); AddEdge(c, a, f);
                }
                foreach (var pair in edges) {
                    var edge = pair.Value;
                    if (edge.count == 1) boundary.Add(BoundaryKey(points[pair.Key.Item1], points[pair.Key.Item2]));
                    if (edge.count > 2) nonManifoldEdges++;
                    if (edge.count == 2 && edge.balance != 0) windingEdges++;
                }
                CheckVertexFans(vertexCount, token);
                MeasureComponents(vertexCount);
            }

            void AddEdge(int a, int b, int face)
            {
                var key = EdgeKey(a, b);
                if (!edges.TryGetValue(key, out var edge)) edges[key] = edge = new Edge { firstFace = face, secondFace = -1 };
                else {
                    components.Union(face, edge.firstFace);
                    if (edge.count == 1) edge.secondFace = face;
                }
                edge.count++; edge.balance += a < b ? 1 : -1;
            }

            int Corner(int face, int slot)
            {
                int first = face * 3;
                for (int k = 0; k < 3; k++) if (slots[indices[first + k]] == slot) return first + k;
                throw new InvalidOperationException("Missing edge endpoint in topology check.");
            }

            void CheckVertexFans(int vertexCount, CancellationToken token)
            {
                var fans = new DisjointSet(indices.Length);
                foreach (var pair in edges) {
                    var edge = pair.Value;
                    if (edge.count != 2) continue;
                    fans.Union(Corner(edge.firstFace, pair.Key.Item1), Corner(edge.secondFace, pair.Key.Item1));
                    fans.Union(Corner(edge.firstFace, pair.Key.Item2), Corner(edge.secondFace, pair.Key.Item2));
                }
                var roots = new int[vertexCount]; var bad = new bool[vertexCount];
                for (int v = 0; v < vertexCount; v++) roots[v] = -1;
                for (int i = 0; i < indices.Length; i++) {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    int v = slots[indices[i]], root = fans.Find(i);
                    if (roots[v] < 0) roots[v] = root;
                    else if (roots[v] != root) bad[v] = true;
                }
                foreach (bool value in bad) if (value) nonManifoldVertices++;
            }

            void MeasureComponents(int vertexCount)
            {
                var values = new Dictionary<int, int>();
                var seen = new bool[vertexCount];
                for (int f = 0; f < components.Count; f++) {
                    int root = components.Find(f);
                    values.TryGetValue(root, out int value); value++;
                    for (int k = 0; k < 3; k++) {
                        int v = slots[indices[f * 3 + k]];
                        if (!seen[v]) { seen[v] = true; value++; }
                    }
                    values[root] = value;
                }
                foreach (var edge in edges.Values) values[components.Find(edge.firstFace)]--;
                euler.AddRange(values.Values); euler.Sort();
            }

            internal bool PreservesBoundary(Snapshot input) => boundary.SetEquals(input.boundary);

            internal bool PreservesComponents(Snapshot input, bool allowPrune)
            {
                if (!allowPrune && euler.Count != input.euler.Count) return false;
                int next = 0;
                foreach (int value in euler) {
                    while (next < input.euler.Count && input.euler[next] < value) next++;
                    if (next == input.euler.Count || input.euler[next++] != value) return false;
                }
                return true;
            }

            static (int, int, int) FaceKey(int a, int b, int c)
            {
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);
                return (a, b, c);
            }

            static (Vector3, Vector3) BoundaryKey(Vector3 a, Vector3 b)
            {
                int order = a.x.CompareTo(b.x);
                if (order == 0) order = a.y.CompareTo(b.y);
                if (order == 0) order = a.z.CompareTo(b.z);
                return order <= 0 ? (a, b) : (b, a);
            }

        }

        internal static Snapshot Inspect(Vector3[] positions, int[] indices, CancellationToken token = default)
            => new Snapshot(positions, indices, token);

        // A volume has no sheet back to trim. Require an oriented closed component
        // with nonzero volume, so two opposite copies of a flat triangle do not qualify.
        internal static bool[] ClosedVolumeFaces(Vector3[] positions, int[] indices, CancellationToken token)
        {
            var data = Inspect(positions, indices, token);
            int count = indices.Length / 3;
            var bad = new bool[count]; var volume = new double[count];
            var low = new Vector3[count]; var high = new Vector3[count]; var seen = new bool[count];
            foreach (var edge in data.edges.Values)
                if (edge.count != 2 || edge.balance != 0) bad[data.components.Find(edge.firstFace)] = true;
            foreach (var list in data.faces.Values)
                if (list.Count > 1) bad[data.components.Find(list[0])] = true;
            Vector3 origin = positions.Length > 0 ? positions[0] : Vector3.zero;
            for (int f = 0; f < count; f++) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                int component = data.components.Find(f);
                var a = positions[indices[f * 3]]; var b = positions[indices[f * 3 + 1]]; var c = positions[indices[f * 3 + 2]];
                if (!MeshGeometry.HasArea(a, b, c)) bad[component] = true;
                if (!seen[component]) { low[component] = high[component] = a; seen[component] = true; }
                low[component] = Vector3.Min(low[component], Vector3.Min(a, Vector3.Min(b, c)));
                high[component] = Vector3.Max(high[component], Vector3.Max(a, Vector3.Max(b, c)));
                double ax = (double)a.x - origin.x, ay = (double)a.y - origin.y, az = (double)a.z - origin.z;
                double bx = (double)b.x - origin.x, by = (double)b.y - origin.y, bz = (double)b.z - origin.z;
                double cx = (double)c.x - origin.x, cy = (double)c.y - origin.y, cz = (double)c.z - origin.z;
                volume[component] += ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
            }
            var result = new bool[count];
            for (int f = 0; f < count; f++) {
                int component = data.components.Find(f);
                var extent = high[component] - low[component];
                double size = Math.Max(extent.x, Math.Max(extent.y, extent.z));
                result[f] = !bad[component] && Math.Abs(volume[component]) > size * size * size * 1e-12;
            }
            return result;
        }

        // A collapsed fin is an opposite-winding duplicate pair. Remove it only
        // when every remaining edge is closed or disappears, and at least one edge
        // attaches it to the rest of the mesh. Never delete one side of a real sheet.
        internal static RemeshNative.IndexedMesh RemoveCollapsedFins(RemeshNative.IndexedMesh mesh, CancellationToken token, out int removed)
        {
            var data = Inspect(mesh.positions, mesh.indices, token);
            var drop = new bool[mesh.TriangleCount]; removed = 0;
            foreach (var pair in data.faces) {
                token.ThrowIfCancellationRequested();
                var list = pair.Value;
                if (list.Count != 2 || Orientation(data, list[0]) == Orientation(data, list[1])) continue;
                var key = pair.Key;
                var keys = new[] { EdgeKey(key.Item1, key.Item2), EdgeKey(key.Item2, key.Item3), EdgeKey(key.Item3, key.Item1) };
                bool safe = true, attached = false;
                foreach (var edgeKey in keys) {
                    var edge = data.edges[edgeKey]; int remaining = edge.count - 2;
                    if (remaining != 0 && (remaining != 2 || edge.balance != 0)) safe = false;
                    if (remaining == 2) attached = true;
                }
                if (!safe || !attached) continue;
                drop[list[0]] = drop[list[1]] = true; removed += 2;
                foreach (var edgeKey in keys) data.edges[edgeKey].count -= 2;
            }
            if (removed == 0) return mesh;
            return WithoutFaces(mesh, drop, removed, token);
        }

        // Solid voxel output can contain entire collapsed two-sided patches.
        // Removing pairs greedily can strand their neighbours or fail at an edge
        // shared by several pairs. Audit and remove each edge-connected patch as
        // one operation. Ordinary Simplify keeps the narrower single-fin policy.
        internal static RemeshNative.IndexedMesh RemoveCollapsedFinPatches(RemeshNative.IndexedMesh mesh, CancellationToken token, out int removed)
        {
            var data = Inspect(mesh.positions, mesh.indices, token);
            var pairs = new List<List<int>>();
            var edges = new List<(int, int)[]>();
            foreach (var entry in data.faces) {
                token.ThrowIfCancellationRequested();
                var list = entry.Value;
                if (list.Count != 2 || Orientation(data, list[0]) == Orientation(data, list[1])) continue;
                var key = entry.Key;
                pairs.Add(list);
                edges.Add(new[] {EdgeKey(key.Item1, key.Item2), EdgeKey(key.Item2, key.Item3), EdgeKey(key.Item3, key.Item1)});
            }
            removed = 0;
            if (pairs.Count == 0) return mesh;
            var groups = new DisjointSet(pairs.Count);
            var first = new Dictionary<(int, int), int>();
            var counts = new Dictionary<(int, int), int>();
            for (int i = 0; i < pairs.Count; ++i) {
                token.ThrowIfCancellationRequested();
                foreach (var edge in edges[i]) {
                    if (first.TryGetValue(edge, out int other)) groups.Union(i, other);
                    else first.Add(edge, i);
                    counts.TryGetValue(edge, out int count); counts[edge] = count + 2;
                }
            }
            var unsafeGroups = new HashSet<int>(); var attachedGroups = new HashSet<int>();
            foreach (var entry in counts) {
                token.ThrowIfCancellationRequested();
                int group = groups.Find(first[entry.Key]);
                var edge = data.edges[entry.Key]; int remaining = edge.count - entry.Value;
                if (edge.balance != 0 || remaining != 0 && remaining != 2) unsafeGroups.Add(group);
                if (remaining == 2) attachedGroups.Add(group);
            }
            var drop = new bool[mesh.TriangleCount];
            for (int i = 0; i < pairs.Count; ++i) {
                token.ThrowIfCancellationRequested();
                int group = groups.Find(i);
                if (unsafeGroups.Contains(group) || !attachedGroups.Contains(group)) continue;
                drop[pairs[i][0]] = drop[pairs[i][1]] = true; removed += 2;
            }
            return removed == 0 ? mesh : WithoutFaces(mesh, drop, removed, token);
        }

        static RemeshNative.IndexedMesh WithoutFaces(RemeshNative.IndexedMesh mesh, bool[] drop, int removed, CancellationToken token)
        {
            var remap = new int[mesh.positions.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var positions = new List<Vector3>(); var indices = new int[mesh.indices.Length - removed * 3]; int write = 0;
            for (int f = 0; f < drop.Length; f++) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                if (drop[f]) continue;
                for (int k = 0; k < 3; k++) {
                    int v = mesh.indices[f * 3 + k];
                    if (remap[v] < 0) { remap[v] = positions.Count; positions.Add(mesh.positions[v]); }
                    indices[write++] = remap[v];
                }
            }
            return new RemeshNative.IndexedMesh { positions = positions.ToArray(), indices = indices };
        }

        static int Orientation(Snapshot data, int face)
        {
            int a = data.slots[data.indices[face * 3]], b = data.slots[data.indices[face * 3 + 1]], c = data.slots[data.indices[face * 3 + 2]];
            return ((a > b ? 1 : 0) + (b > c ? 1 : 0) + (a > c ? 1 : 0)) & 1;
        }

        static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);
    }
}
