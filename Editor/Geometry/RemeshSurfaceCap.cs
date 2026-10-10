using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Explicit disk filling on a curved 3D rim. Fixed source vertices,
    /// bounded ear search, and atomic acceptance after topology/contact audits.</summary>
    internal static class RemeshSurfaceCap
    {
        const int MaxRimEdges = 64;
        const int MaxCandidates = 4096;

        sealed class Search
        {
            internal Vector3[] positions;
            internal RemeshCapIntersection.Q[][] exact;
            internal CancellationToken token;
            internal int trials, candidates, originalFaces, initialBoundary, rimEdges, limit;
            internal string lastFailure;
        }

        readonly struct Ear
        {
            internal readonly int slot;
            internal readonly double score;
            internal Ear(int slot, double score) { this.slot = slot; this.score = score; }
        }

        internal static int[] Generate(Vector3[] positions, int[] source, List<int> loop, CancellationToken token,
            ref int trials, out int tests, RemeshPlanarCap.ExternalContacts external = null, int maxCandidates = MaxCandidates)
        {
            token.ThrowIfCancellationRequested(); tests = 0;
            if (loop.Count < 3 || loop.Count > MaxRimEdges) throw Refuse("disk rim must contain 3 to 64 edges");
            if (maxCandidates < 1 || maxCandidates > MaxCandidates) throw Refuse("invalid candidate budget");
            var topology = RemeshTopology.Inspect(positions, source, token);
            var search = new Search {positions = positions, token = token, trials = trials, originalFaces = source.Length / 3,
                initialBoundary = topology.boundary.Count, rimEdges = loop.Count, limit = maxCandidates,
                exact = new RemeshCapIntersection.Q[positions.Length][]};
            for (int i = 0; i < positions.Length; ++i) search.exact[i] = RemeshCapIntersection.Point(positions[i]);
            try {
                if (!Fill(search, new List<int>(loop), new List<int>(source), external?.Fork(), out var result, out var contacts))
                    throw Refuse("no complete audited triangulation" + (search.lastFailure == null ? "" : ": " + search.lastFailure));
                // Re-audit the entire accepted patch before exporting any triangles or
                // statistics. Failed search branches never alter the caller's arrays.
                RemeshPlanarCap.AuditContacts(positions, search.exact, result.ToArray(), search.originalFaces,
                    token, ref search.trials, out tests, contacts);
                external?.Merge(contacts);
                return result.GetRange(source.Length, result.Count - source.Length).ToArray();
            }
            finally { trials = search.trials; }
        }

        static bool Fill(Search search, List<int> ring, List<int> faces, RemeshPlanarCap.ExternalContacts contacts,
            out List<int> result, out RemeshPlanarCap.ExternalContacts acceptedContacts)
        {
            search.token.ThrowIfCancellationRequested(); result = null; acceptedContacts = null;
            var topology = RemeshTopology.Inspect(search.positions, faces.ToArray(), search.token);
            var ears = RankedEars(search.positions, ring, topology);
            foreach (var ear in ears) {
                search.token.ThrowIfCancellationRequested();
                if (++search.candidates > search.limit) throw Refuse("candidate search budget exceeded; no partial patch accepted");
                int a = ring[(ear.slot + ring.Count - 1) % ring.Count], b = ring[ear.slot], c = ring[(ear.slot + 1) % ring.Count];
                var candidate = new List<int>(faces) {a,c,b};
                var next = new List<int>(ring); next.RemoveAt(ear.slot);
                var current = RemeshTopology.Inspect(search.positions, candidate.ToArray(), search.token);
                int remaining = ring.Count == 3 ? 0 : next.Count;
                if (!current.Valid || current.boundary.Count != search.initialBoundary - search.rimEdges + remaining) continue;
                var branch = contacts?.Fork();
                try {
                    RemeshPlanarCap.AuditContacts(search.positions, search.exact, candidate.ToArray(), faces.Count / 3,
                        search.token, ref search.trials, out _, branch);
                }
                catch (InvalidOperationException failure) when (!failure.Message.Contains("budget")) {
                    search.lastFailure = failure.Message; continue;
                }
                if (ring.Count == 3) { result = candidate; acceptedContacts = branch; return true; }
                if (Fill(search, next, candidate, branch, out result, out acceptedContacts)) return true;
            }
            return false;
        }

        static List<Ear> RankedEars(Vector3[] positions, List<int> ring, RemeshTopology.Snapshot topology)
        {
            var normals = MeshGeometry.FaceNormals(positions, topology.indices);
            var ears = new List<Ear>(ring.Count);
            for (int slot = 0; slot < ring.Count; ++slot) {
                int a = ring[(slot + ring.Count - 1) % ring.Count], b = ring[slot], c = ring[(slot + 1) % ring.Count];
                var u = positions[c] - positions[a]; var v = positions[b] - positions[a];
                var normal = Vector3.Cross(u,v);
                double twiceArea = normal.magnitude;
                double sum = (double)u.sqrMagnitude + v.sqrMagnitude + (positions[c]-positions[b]).sqrMagnitude;
                if (!(twiceArea > 0) || !(sum > 0)) continue;
                normal.Normalize();
                // Prefer compact triangles and continuity against the actual donor
                // or earlier Cap face adjacent to each consumed boundary edge.
                double score = 2 * Math.Sqrt(3) * twiceArea / sum;
                foreach (var edge in new[] {(a,b),(b,c)}) {
                    var key = edge.Item1 < edge.Item2 ? edge : (edge.Item2,edge.Item1);
                    if (topology.edges.TryGetValue(key, out var adjacent))
                        score += .25 * Vector3.Dot(normal,normals[adjacent.firstFace]);
                }
                ears.Add(new Ear(slot,score));
            }
            ears.Sort((a,b) => {
                int comparison = b.score.CompareTo(a.score);
                return comparison != 0 ? comparison : ring[a.slot].CompareTo(ring[b.slot]);
            });
            return ears;
        }

        static InvalidOperationException Refuse(string reason) => new InvalidOperationException("Surface Cap refused: " + reason + ". Source donors were preserved.");
    }
}
