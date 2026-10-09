using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Opt-in planar disks on a welded, geometry-only support. Original
    /// donor arrays are never mutated. Ambiguous/invalid candidates fail atomically.</summary>
    internal static class RemeshPlanarCap
    {
        internal const int Revision = 2;
        const int MaxVertices = 200000, MaxIndices = 1200000, MaxLoopEdges = 512;
        const int MaxPairTrials = 2000000;

        internal sealed class Support
        {
            internal Vector3[] positions;
            internal int[] indices;
            internal int originalFaces, weldedVertices, loops, addedFaces, contactTests, localPatches, planeRechecks;
            internal string selection;
            internal string Description => $"welded {weldedVertices} vertices; {loops} boundary loops; disk selection {selection}; added {addedFaces} faces; " +
                $"{localPatches} local patches; {planeRechecks} fresh plane checks; {contactTests} exact contact tests";
        }

        internal static Support Prepare(Vector3[] positions, int[] indices, string selection, CancellationToken token, bool localPlanes = false)
        {
            token.ThrowIfCancellationRequested();
            if (positions == null || indices == null || positions.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
                throw new ArgumentException("Cap requires indexed triangle geometry.");
            if (positions.Length > MaxVertices || indices.Length > MaxIndices)
                throw Refuse("source geometry exceeds the preparation budget");
            foreach (var p in positions) if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                throw Refuse("source positions are not finite");
            foreach (int v in indices) if (v < 0 || v >= positions.Length) throw Refuse("source index is out of range");

            // This is the shared position-weld primitive used by mesh connectivity.
            // UV-aware UvEdgeWeld deliberately preserves seams; those seams must
            // not appear as physical holes in this geometry-only preparation.
            var slots = MeshGeometry.WeldPositions(positions, out int count);
            var pWeld = new Vector3[count]; var iWeld = new int[indices.Length];
            for (int i = 0; i < positions.Length; ++i) pWeld[slots[i]] = positions[i];
            for (int i = 0; i < indices.Length; ++i) iWeld[i] = slots[indices[i]];
            var topology = RemeshTopology.Inspect(pWeld, iWeld, token);
            if (!topology.Valid) throw Refuse("source after weld: " + topology.Description);
            var loops = Boundaries(topology, token);
            var result = new Support { positions = pWeld, indices = iWeld, originalFaces = iWeld.Length / 3,
                weldedVertices = positions.Length - count, loops = loops.Count, selection = selection };
            if (loops.Count == 0) return result;
            var chosen = Selection(selection, loops.Count);
            var exact = new RemeshCapIntersection.Q[pWeld.Length][];
            for (int i = 0; i < exact.Length; ++i) exact[i] = RemeshCapIntersection.Point(pWeld[i]);
            var assembled = new List<int>(iWeld); int contactTrials = 0;
            foreach (int loop in chosen) {
                token.ThrowIfCancellationRequested();
                if (localPlanes) CloseLocal(pWeld, exact, assembled, loops[loop], token, result, ref contactTrials);
                else assembled.AddRange(Triangulate(pWeld, exact, loops[loop], token));
                if (assembled.Count - iWeld.Length > MaxLoopEdges * 3 * 8) throw Refuse("total Cap face budget exceeded");
            }
            var allIndices = assembled.ToArray();
            var after = RemeshTopology.Inspect(pWeld, allIndices, token);
            int removedEdges = 0; foreach (int loop in chosen) removedEdges += loops[loop].Count;
            if (!after.Valid || after.boundary.Count != topology.boundary.Count - removedEdges)
                throw Refuse("assembled Cap topology: " + after.Description);
            if (!localPlanes) AuditContacts(pWeld, exact, allIndices, iWeld.Length / 3, token, ref contactTrials, out result.contactTests);
            result.indices = allIndices; result.addedFaces = (allIndices.Length - iWeld.Length) / 3;
            return result;
        }

        static void CloseLocal(Vector3[] p, RemeshCapIntersection.Q[][] exact, List<int> assembled,
            List<int> loop, CancellationToken token, Support result, ref int contactTrials)
        {
            var analysis = RemeshCapPlanes.Analyze(p, loop, token); ++result.planeRechecks;
            if (analysis.kind == RemeshCapPlanes.Kind.Planar) {
                AppendPatch(p, exact, assembled, loop, true, token, result, ref contactTrials);
                return;
            }
            if (analysis.kind != RemeshCapPlanes.Kind.TwoPlanes)
                throw Refuse($"local contour has {analysis.hypotheses} supported two-plane partitions ({analysis.kind}); " +
                    "no unique local closure is established; three or more planes and Bridge require separate intent");
            var arc = analysis.firstArc;
            AppendPatch(p, exact, assembled, arc, false, token, result, ref contactTrials);
            // Accepting the first patch changes the actual halfedge contour. Use
            // that new topology, not the stale second arc or guessed loop number.
            var fresh = Boundaries(RemeshTopology.Inspect(p, assembled.ToArray(), token), token);
            List<int> remaining = null; int a = arc[0], b = arc[arc.Count - 1];
            foreach (var candidate in fresh) for (int i = 0; i < candidate.Count; ++i) {
                int x = candidate[i], y = candidate[(i + 1) % candidate.Count];
                if (x != a || y != b) continue;
                if (remaining != null) throw Refuse("the new closure chord belongs to more than one boundary");
                remaining = candidate;
            }
            if (remaining == null) throw Refuse("the new closure chord is missing from the remaining contour");
            var next = RemeshCapPlanes.Analyze(p, remaining, token); ++result.planeRechecks;
            if (next.kind != RemeshCapPlanes.Kind.Planar) throw Refuse("the remaining local contour is not wholly planar after the first patch");
            AppendPatch(p, exact, assembled, remaining, true, token, result, ref contactTrials);
        }

        static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

        static HashSet<(int, int)> BoundaryEdges(RemeshTopology.Snapshot topology)
        {
            var result = new HashSet<(int, int)>();
            foreach (var pair in topology.edges) if (pair.Value.count == 1) result.Add(pair.Key);
            return result;
        }

        static void AppendPatch(Vector3[] p, RemeshCapIntersection.Q[][] exact, List<int> assembled,
            List<int> arc, bool closed, CancellationToken token, Support result, ref int trials)
        {
            int oldFaces = assembled.Count / 3;
            var before = RemeshTopology.Inspect(p, assembled.ToArray(), token);
            var expected = BoundaryEdges(before);
            int edgeCount = closed ? arc.Count : arc.Count - 1;
            // The arc is an ordered portion of the freshly extracted boundary.
            // Its endpoints share one new chord; no other rim edge may change.
            for (int i = 0; i < edgeCount; ++i) {
                int a = arc[i], b = arc[(i + 1) % arc.Count];
                var edge = before.edges[EdgeKey(a, b)];
                var ix = before.indices; int f = edge.firstFace;
                bool directed = false;
                for (int k = 0; k < 3; ++k) if (ix[f * 3 + k] == a && ix[f * 3 + (k + 1) % 3] == b) directed = true;
                if (edge.count != 1 || !directed || !expected.Remove(EdgeKey(a, b)))
                    throw Refuse("local patch support is not a continuous directed boundary arc");
            }
            if (!closed && !expected.Add(EdgeKey(arc[0], arc[arc.Count - 1])))
                throw Refuse("local closure chord is already a boundary edge");
            var added = Triangulate(p, exact, arc, token);
            if (assembled.Count + added.Length - result.originalFaces * 3 > MaxLoopEdges * 3 * 8)
                throw Refuse("total Cap face budget exceeded");
            var candidate = new List<int>(assembled); candidate.AddRange(added);
            var all = candidate.ToArray(); var after = RemeshTopology.Inspect(p, all, token);
            if (!after.Valid || !BoundaryEdges(after).SetEquals(expected))
                throw Refuse("local patch changes an unselected boundary or has invalid topology: " + after.Description);
            AuditContacts(p, exact, all, oldFaces, token, ref trials, out int contacts);
            result.contactTests += contacts;
            assembled.AddRange(added); ++result.localPatches;
        }

        // All vertices must have one fan (the preflight above). Each boundary
        // slot then has exactly one incoming/outgoing halfedge, no junction pairing.
        static List<List<int>> Boundaries(RemeshTopology.Snapshot topology, CancellationToken token)
        {
            var next = new SortedDictionary<int, int>(); var incoming = new HashSet<int>();
            var ix = topology.indices;
            for (int f = 0; f < ix.Length / 3; ++f) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; ++k) {
                    int a = ix[f * 3 + k], b = ix[f * 3 + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (topology.edges[key].count != 1) continue;
                    if (next.ContainsKey(a) || !incoming.Add(b)) throw Refuse("boundary junction is not a continuous single fan");
                    next.Add(a, b);
                }
            }
            var visited = new HashSet<int>(); var loops = new List<List<int>>();
            foreach (int start in next.Keys) {
                if (visited.Contains(start)) continue;
                var loop = new List<int>(); int current = start;
                do {
                    if (!visited.Add(current) || !next.TryGetValue(current, out int following)) throw Refuse("boundary is not a continuous cycle");
                    loop.Add(current); current = following;
                    if (loop.Count > MaxLoopEdges) throw Refuse("a boundary loop exceeds 512 edges");
                } while (current != start);
                if (loop.Count < 3) throw Refuse("boundary cycle has fewer than three vertices");
                loops.Add(loop);
            }
            return loops;
        }

        static SortedSet<int> Selection(string text, int count)
        {
            var result = new SortedSet<int>();
            foreach (string part in (text ?? "").Split(',')) {
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int loop) || loop < 0 || loop >= count || !result.Add(loop))
                    throw Refuse($"disk selection '{text}' must list unique loop numbers from 0 to {count - 1}");
            }
            return result;
        }

        static int[] Triangulate(Vector3[] p, RemeshCapIntersection.Q[][] exact, List<int> loop, CancellationToken token)
        {
            if (!RemeshCapPlanes.TryPlane(p, loop, -1, true, out var plane))
                throw Refuse("selected boundary is not wholly planar or is collinear; local compound Cap or Bridge is required");
            int drop = plane.drop;
            int ax = (drop + 1) % 3, ay = (drop + 2) % 3;
            var area = new RemeshCapIntersection.Q(0);
            for (int i = 0; i < loop.Count; ++i) area += RemeshCapIntersection.Orient(exact[loop[0]], exact[loop[i]], exact[loop[(i + 1) % loop.Count]], ax, ay);
            int sign = area.Sign;
            if (sign == 0) throw Refuse("projected boundary has zero area");
            CheckSimple(exact, loop, ax, ay, token);
            var ring = new List<int>(loop); var triangles = new List<int>(); int trials = 0;
            while (ring.Count > 3) {
                token.ThrowIfCancellationRequested(); bool ear = false;
                for (int k = 0; k < ring.Count; ++k) {
                    int a = ring[(k + ring.Count - 1) % ring.Count], b = ring[k], c = ring[(k + 1) % ring.Count];
                    if (RemeshCapIntersection.Orient(exact[a], exact[b], exact[c], ax, ay).Sign * sign <= 0) continue;
                    bool blocked = false;
                    foreach (int v in ring) {
                        if ((++trials & 255) == 0) token.ThrowIfCancellationRequested();
                        if (trials > MaxPairTrials) throw Refuse("triangulation search exceeds the operation budget");
                        if (v == a || v == b || v == c) continue;
                        if (RemeshCapIntersection.Orient(exact[a], exact[b], exact[v], ax, ay).Sign * sign >= 0 &&
                            RemeshCapIntersection.Orient(exact[b], exact[c], exact[v], ax, ay).Sign * sign >= 0 &&
                            RemeshCapIntersection.Orient(exact[c], exact[a], exact[v], ax, ay).Sign * sign >= 0) { blocked = true; break; }
                    }
                    if (blocked) continue;
                    // Reverse the source boundary winding. Every rim edge occurs
                    // once in the Cap in the opposite direction.
                    triangles.Add(a); triangles.Add(c); triangles.Add(b); ring.RemoveAt(k); ear = true; break;
                }
                if (!ear) throw Refuse("constrained planar triangulation stalled; no vertices were removed");
            }
            if (RemeshCapIntersection.Orient(exact[ring[0]], exact[ring[1]], exact[ring[2]], ax, ay).Sign * sign <= 0)
                throw Refuse("final constrained triangle is degenerate or inverted");
            triangles.Add(ring[0]); triangles.Add(ring[2]); triangles.Add(ring[1]);
            ImproveDiagonals(exact, triangles, ax, ay, token);
            return triangles.ToArray();
        }

        // Lawson flips produce a constrained Delaunay triangulation in the chosen
        // projection. Only shared interior edges may flip; every rim edge remains
        // fixed. Exact incircle signs avoid roundoff oscillation and skinny fan ears.
        static void ImproveDiagonals(RemeshCapIntersection.Q[][] p, List<int> ix, int x, int y, CancellationToken token)
        {
            int faces = ix.Count / 3;
            for (int pass = 0; pass < faces * faces; ++pass) {
                token.ThrowIfCancellationRequested(); bool changed = false;
                var edges = new Dictionary<(int, int), (int face, int corner)>();
                for (int f = 0; f < faces && !changed; ++f) for (int k = 0; k < 3; ++k) {
                    int a = ix[f * 3 + k], b = ix[f * 3 + (k + 1) % 3], c = ix[f * 3 + (k + 2) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var previous)) { edges.Add(key, (f, k)); continue; }
                    int g = previous.face, d = ix[g * 3 + (previous.corner + 2) % 3];
                    int sign = RemeshCapIntersection.Orient(p[a], p[b], p[c], x, y).Sign;
                    if (RemeshCapIntersection.Orient(p[c], p[d], p[b], x, y).Sign != sign ||
                        RemeshCapIntersection.Orient(p[d], p[c], p[a], x, y).Sign != sign) continue;
                    var ax = p[a][x] - p[d][x]; var ay = p[a][y] - p[d][y];
                    var bx = p[b][x] - p[d][x]; var by = p[b][y] - p[d][y];
                    var cx = p[c][x] - p[d][x]; var cy = p[c][y] - p[d][y];
                    var circle = (ax * ax + ay * ay) * (bx * cy - by * cx) -
                        (bx * bx + by * by) * (ax * cy - ay * cx) + (cx * cx + cy * cy) * (ax * by - ay * bx);
                    if (circle.Sign * sign <= 0) continue;
                    ix[f * 3] = c; ix[f * 3 + 1] = d; ix[f * 3 + 2] = b;
                    ix[g * 3] = d; ix[g * 3 + 1] = c; ix[g * 3 + 2] = a;
                    changed = true; break;
                }
                if (!changed) return;
            }
            throw Refuse("constrained diagonal refinement exceeded its budget");
        }

        static void CheckSimple(RemeshCapIntersection.Q[][] p, List<int> loop, int x, int y, CancellationToken token)
        {
            for (int i = 0; i < loop.Count; ++i) {
                token.ThrowIfCancellationRequested();
                int iNext = (i + 1) % loop.Count;
                for (int j = i + 1; j < loop.Count; ++j) {
                    int jNext = (j + 1) % loop.Count;
                    if (iNext == j || jNext == i) continue;
                    var a = p[loop[i]]; var b = p[loop[iNext]]; var c = p[loop[j]]; var d = p[loop[jNext]];
                    int ac = RemeshCapIntersection.Orient(a, b, c, x, y).Sign, ad = RemeshCapIntersection.Orient(a, b, d, x, y).Sign;
                    int ca = RemeshCapIntersection.Orient(c, d, a, x, y).Sign, cb = RemeshCapIntersection.Orient(c, d, b, x, y).Sign;
                    if (ac * ad > 0 || ca * cb > 0) continue;
                    if (Separated(a, b, c, d, x) || Separated(a, b, c, d, y)) continue;
                    throw Refuse("projected boundary self-intersects or touches itself");
                }
            }
        }

        static bool Separated(RemeshCapIntersection.Q[] a, RemeshCapIntersection.Q[] b,
            RemeshCapIntersection.Q[] c, RemeshCapIntersection.Q[] d, int axis)
        {
            var loA = a[axis].CompareTo(b[axis]) < 0 ? a[axis] : b[axis]; var hiA = a[axis].CompareTo(b[axis]) > 0 ? a[axis] : b[axis];
            var loB = c[axis].CompareTo(d[axis]) < 0 ? c[axis] : d[axis]; var hiB = c[axis].CompareTo(d[axis]) > 0 ? c[axis] : d[axis];
            return hiA.CompareTo(loB) < 0 || hiB.CompareTo(loA) < 0;
        }

        static void AuditContacts(Vector3[] positions, RemeshCapIntersection.Q[][] exact, int[] ix, int originalFaces,
            CancellationToken token, ref int trials, out int tests)
        {
            int faces = ix.Length / 3; tests = 0;
            var low = new Vector3[faces]; var high = new Vector3[faces];
            for (int f = 0; f < faces; ++f) {
                var a = positions[ix[f * 3]]; var b = positions[ix[f * 3 + 1]]; var c = positions[ix[f * 3 + 2]];
                low[f] = Vector3.Min(a, Vector3.Min(b, c)); high[f] = Vector3.Max(a, Vector3.Max(b, c));
            }
            for (int f = originalFaces; f < faces; ++f) for (int g = 0; g < f; ++g) {
                if ((trials & 255) == 0) token.ThrowIfCancellationRequested();
                if (++trials > MaxPairTrials) throw Refuse("Cap contact audit exceeds the pair budget");
                if (low[f].x > high[g].x || low[g].x > high[f].x || low[f].y > high[g].y || low[g].y > high[f].y || low[f].z > high[g].z || low[g].z > high[f].z) continue;
                ++tests;
                var a = new[] { exact[ix[f * 3]], exact[ix[f * 3 + 1]], exact[ix[f * 3 + 2]] };
                var b = new[] { exact[ix[g * 3]], exact[ix[g * 3 + 1]], exact[ix[g * 3 + 2]] };
                if (RemeshCapIntersection.Improper(a, b)) throw Refuse($"new face {f} contacts face {g} beyond their shared vertex/edge");
            }
        }

        static InvalidOperationException Refuse(string reason) => new InvalidOperationException("Planar Cap refused: " + reason + ". Source donors were preserved.");
    }
}
