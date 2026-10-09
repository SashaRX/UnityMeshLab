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
        internal const int Revision = 1;
        const int MaxVertices = 200000, MaxIndices = 1200000, MaxLoopEdges = 512;
        const int MaxPairTrials = 2000000;

        internal sealed class Support
        {
            internal Vector3[] positions;
            internal int[] indices;
            internal int originalFaces, weldedVertices, loops, addedFaces, contactTests;
            internal string selection;
            internal string Description => $"welded {weldedVertices} vertices; {loops} boundary loops; disk selection {selection}; added {addedFaces} faces; {contactTests} exact contact tests";
        }

        internal static Support Prepare(Vector3[] positions, int[] indices, string selection, CancellationToken token)
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
            var assembled = new List<int>(iWeld);
            foreach (int loop in chosen) {
                token.ThrowIfCancellationRequested();
                var added = Triangulate(pWeld, exact, loops[loop], token);
                assembled.AddRange(added);
                if (assembled.Count - iWeld.Length > MaxLoopEdges * 3 * 8) throw Refuse("total Cap face budget exceeded");
            }
            var allIndices = assembled.ToArray();
            var after = RemeshTopology.Inspect(pWeld, allIndices, token);
            int removedEdges = 0; foreach (int loop in chosen) removedEdges += loops[loop].Count;
            if (!after.Valid || after.boundary.Count != topology.boundary.Count - removedEdges)
                throw Refuse("assembled Cap topology: " + after.Description);
            AuditContacts(pWeld, exact, allIndices, iWeld.Length / 3, token, out result.contactTests);
            result.indices = allIndices; result.addedFaces = (allIndices.Length - iWeld.Length) / 3;
            return result;
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
            var origin = p[loop[0]];
            // Choose the strongest cross product with the longest chord. This
            // avoids allowing nearly collinear first corners to select a plane.
            int far = loop[0]; double longest = 0;
            foreach (int v in loop) {
                double dx = (double)p[v].x - origin.x, dy = (double)p[v].y - origin.y, dz = (double)p[v].z - origin.z;
                double length = dx * dx + dy * dy + dz * dz;
                if (length > longest) { longest = length; far = v; }
            }
            double ux = (double)p[far].x - origin.x, uy = (double)p[far].y - origin.y, uz = (double)p[far].z - origin.z;
            double nx = 0, ny = 0, nz = 0, best = 0;
            foreach (int v in loop) {
                double vx = (double)p[v].x - origin.x, vy = (double)p[v].y - origin.y, vz = (double)p[v].z - origin.z;
                double x = uy * vz - uz * vy, y = uz * vx - ux * vz, z = ux * vy - uy * vx;
                double length = x * x + y * y + z * z;
                if (length > best) { best = length; nx = x; ny = y; nz = z; }
            }
            if (!(best > longest * longest * 1e-20)) throw Refuse("boundary is collinear; a plane cannot be selected");
            double norm = Math.Sqrt(best), tolerance = Math.Sqrt(longest) * 1e-5;
            foreach (int v in loop) {
                double residual = Math.Abs(nx * ((double)p[v].x - origin.x) + ny * ((double)p[v].y - origin.y) + nz * ((double)p[v].z - origin.z)) / norm;
                if (residual > tolerance) throw Refuse("selected boundary is not wholly planar; local compound Cap or Bridge is required");
            }
            int drop = Math.Abs(nx) >= Math.Abs(ny) && Math.Abs(nx) >= Math.Abs(nz) ? 0 : Math.Abs(ny) >= Math.Abs(nz) ? 1 : 2;
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
            CancellationToken token, out int tests)
        {
            int faces = ix.Length / 3, trials = 0; tests = 0;
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
