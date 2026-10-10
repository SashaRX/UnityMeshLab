using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Opt-in planar disks on a welded, geometry-only support. Original
    /// donor arrays are never mutated. Each closure candidate is accepted atomically.</summary>
    internal static class RemeshPlanarCap
    {
        internal const int Revision = 15;
        const int MaxVertices = 200000, MaxIndices = 1200000, MaxLoopEdges = 512;
        const int MaxPairTrials = 2000000;

        internal sealed class ExternalContacts
        {
            internal int[] faceOwners;
            internal HashSet<int> closingOwners;
            internal int[] faceElements;
            internal HashSet<int> closingElements;
            internal long excludedElementPairs;
            internal int count, firstNewFace = -1, firstSourceFace = -1;
            internal ExternalContacts Fork(HashSet<int> owners = null, int[] elements = null, HashSet<int> closing = null) =>
                new ExternalContacts { faceOwners = faceOwners, closingOwners = owners ?? closingOwners,
                    faceElements = elements ?? faceElements, closingElements = closing ?? closingElements };
            internal bool IsExternal(int face)
            {
                if (faceElements != null) return face < faceElements.Length && faceElements[face] >= 0 &&
                    closingElements != null && closingElements.Count > 0 && !closingElements.Contains(faceElements[face]);
                return faceOwners != null && face < faceOwners.Length && faceOwners[face] >= 0 &&
                    closingOwners != null && closingOwners.Count > 0 && !closingOwners.Contains(faceOwners[face]);
            }
            internal void Add(int addedFace, int sourceFace)
            {
                if (count++ == 0) { firstNewFace = addedFace; firstSourceFace = sourceFace; }
            }
            internal void Merge(ExternalContacts accepted)
            {
                if (accepted == null) return;
                excludedElementPairs += accepted.excludedElementPairs;
                if (accepted.count == 0) return;
                if (count == 0) { firstNewFace = accepted.firstNewFace; firstSourceFace = accepted.firstSourceFace; }
                count += accepted.count;
            }
        }

        internal sealed class Support
        {
            internal Vector3[] positions;
            internal int[] indices;
            internal int originalFaces, weldedVertices, loops, addedFaces, contactTests, localPatches, planeRechecks;
            internal int remainingBoundaryEdges;
            internal string selection;
            internal int[] facePatches;
            internal int[] faceElements;
            internal int[][] boundaryLoops;
            internal ExternalContacts externalContacts;
            internal readonly Dictionary<int, string> loopFailures = new Dictionary<int, string>();
            internal readonly Dictionary<int, int> bridgePartners = new Dictionary<int, int>();
            internal readonly Dictionary<int, string> bridgeSearch = new Dictionary<int, string>();
            internal readonly Dictionary<int, string> bridgeCapFallbacks = new Dictionary<int, string>();
            internal string selectionWarning;
            internal readonly List<int> patchEnds = new List<int>();
            internal string Description => $"welded {weldedVertices} vertices; {loops} boundary loops; selection {selection}; added {addedFaces} faces; " +
                $"{localPatches} local patches; {planeRechecks} fresh plane checks; {contactTests} exact contact tests; " +
                (externalContacts?.faceElements != null ? $"{externalContacts.excludedElementPairs} pairs with other elements excluded from contact audit" :
                    $"{externalContacts?.count ?? 0} non-blocking contacts with other source meshes") + $"; {loopFailures.Count} refused loops" +
                $"; {bridgeCapFallbacks.Count} Bridge-to-Cap attempts" + (selectionWarning == null ? "" : "; " + selectionWarning);
        }

        internal static Support Prepare(Vector3[] positions, int[] indices, string selection, CancellationToken token, bool localPlanes = false,
            RemeshClosureMode mode = RemeshClosureMode.Caps, double planeTolerance = 0, int[] sourceFaceOwners = null,
            Action<int, int, int> loopCompleted = null, bool continueOnRefusal = false, bool elementScopedContacts = false,
            bool bridgeCapFallback = false)
        {
            token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(typeof(RemeshClosureMode),mode)) throw Refuse("unknown closure method");
            if (!double.IsFinite(planeTolerance) || planeTolerance < 0) throw Refuse("plane tolerance must be finite and nonnegative");
            if (positions == null || indices == null || positions.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
                throw new ArgumentException("Cap requires indexed triangle geometry.");
            if (positions.Length > MaxVertices || indices.Length > MaxIndices)
                throw Refuse("source geometry exceeds the preparation budget");
            foreach (var p in positions) {
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                    throw Refuse("source positions are not finite");
            }
            foreach (int v in indices) if (v < 0 || v >= positions.Length) throw Refuse("source index is out of range");
            if (sourceFaceOwners != null && sourceFaceOwners.Length != indices.Length / 3)
                throw new ArgumentException("Source face ownership must match the donor face count.");

            // This is the shared position-weld primitive used by mesh connectivity.
            // UV-aware UvEdgeWeld deliberately preserves seams; those seams must
            // not appear as physical holes in this geometry-only preparation.
            var slots = MeshGeometry.WeldPositions(positions, out int count);
            var pWeld = new Vector3[count]; var iWeld = new int[indices.Length];
            for (int i = 0; i < positions.Length; ++i) pWeld[slots[i]] = positions[i];
            for (int i = 0; i < indices.Length; ++i) iWeld[i] = slots[indices[i]];
            var topology = RemeshTopology.Inspect(pWeld, iWeld, token);
            if (!topology.Valid) throw Refuse("source after weld: " + topology.Description);
            var loops = Boundaries(topology, token, !continueOnRefusal);
            var result = new Support { positions = pWeld, indices = iWeld, originalFaces = iWeld.Length / 3,
                weldedVertices = positions.Length - count, loops = loops.Count, selection = selection };
            result.boundaryLoops = loops.ConvertAll(loop => loop.ToArray()).ToArray();
            if (sourceFaceOwners != null) result.externalContacts = new ExternalContacts { faceOwners = (int[])sourceFaceOwners.Clone() };
            if (elementScopedContacts) {
                result.externalContacts ??= new ExternalContacts();
                result.externalContacts.faceElements = ElementIds(topology, token);
                result.faceElements = result.externalContacts.faceElements;
            }
            if (loops.Count == 0) return result;
            var chosen = Selection(selection, loops.Count, continueOnRefusal, out result.selectionWarning);
            // An explicit Bridge needs exactly the authored pair. Do not silently
            // reinterpret an invalid three-entry selection as a different pair.
            if (mode == RemeshClosureMode.Bridge && result.selectionWarning != null) {
                chosen.Clear();
                result.selectionWarning += " Bridge was not attempted: select exactly two unique available loop numbers.";
            }
            var exact = new RemeshCapIntersection.Q[pWeld.Length][];
            for (int i = 0; i < exact.Length; ++i) exact[i] = RemeshCapIntersection.Point(pWeld[i]);
            var assembled = new List<int>(iWeld); int contactTrials = 0;
            var currentTopology = topology;
            var finished = new HashSet<int>();
            var closedLoops = new HashSet<int>();
            var partners = new Dictionary<int,int>();
            var ambiguous = new HashSet<int>();
            string selectionRefusal = null;
            if (mode == RemeshClosureMode.Automatic) {
                if (chosen.Count > 16) selectionRefusal = "automatic closure is limited to 16 selected loops; split the operation or select explicit Caps";
                if (selectionRefusal != null && !continueOnRefusal) throw Refuse(selectionRefusal);
                if (selectionRefusal == null) {
                    var normals = MeshGeometry.FaceNormals(pWeld,iWeld);
                    foreach (int a in chosen) foreach (int b in chosen) {
                        if (b <= a) continue;
                        if (!RemeshBridge.ContinuesToward(pWeld,topology,normals,loops[a],loops[b]) ||
                            !RemeshBridge.ContinuesToward(pWeld,topology,normals,loops[b],loops[a])) continue;
                        if (partners.ContainsKey(a) || partners.ContainsKey(b)) {
                            if (!continueOnRefusal && !bridgeCapFallback) throw Refuse("automatic closure has more than one collar partner; select Bridge intent explicitly");
                            ambiguous.Add(a); ambiguous.Add(b);
                            if (partners.TryGetValue(a, out int oldA)) ambiguous.Add(oldA);
                            if (partners.TryGetValue(b, out int oldB)) ambiguous.Add(oldB);
                            continue;
                        }
                        partners.Add(a,b); partners.Add(b,a);
                    }
                }
            }
            if (mode == RemeshClosureMode.Bridge && chosen.Count != 2) {
                selectionRefusal = "Bridge requires exactly two selected loops";
                if (!continueOnRefusal) throw Refuse(selectionRefusal);
            }
            if (mode == RemeshClosureMode.Automatic && bridgeCapFallback) {
                foreach (int loop in ambiguous) {
                    // No pair is selected and no Bridge geometry was generated.
                    // The explicit fallback authorizes independent planar disks,
                    // not guessing a partner from the ambiguous candidate graph.
                    result.bridgeCapFallbacks.Add(loop, Refuse("automatic closure has more than one collar partner; select Bridge intent explicitly").Message);
                    partners.Remove(loop);
                }
            }
            foreach (int loop in chosen) {
                token.ThrowIfCancellationRequested();
                if (finished.Contains(loop)) continue;
                int partner = -1;
                if (!result.bridgeCapFallbacks.ContainsKey(loop)) {
                    if (mode == RemeshClosureMode.Bridge && chosen.Count == 2) { foreach (int other in chosen) if (other != loop) partner = other; }
                    else if (mode == RemeshClosureMode.Automatic && partners.TryGetValue(loop,out int paired)) partner = paired;
                }
                while (true) {
                    bool bridgeAttempted = false;
                    try {
                        if (selectionRefusal != null) throw Refuse(selectionRefusal);
                        if (!result.bridgeCapFallbacks.ContainsKey(loop) && (ambiguous.Contains(loop) || partner >= 0 && ambiguous.Contains(partner)))
                            throw Refuse("automatic closure has more than one collar partner; select Bridge intent explicitly");
                        if (loops[loop].Count > MaxLoopEdges || partner >= 0 && loops[partner].Count > MaxLoopEdges)
                            throw Refuse("a selected boundary loop exceeds 512 edges");
                        if (contactTrials > MaxPairTrials) throw Refuse("Cap contact audit exceeds the pair budget");
                        var owners = ClosingOwners(topology, loops[loop], partner >= 0 ? loops[partner] : null, sourceFaceOwners);
                        var external = owners == null ? null : result.externalContacts?.Fork(owners);
                        if (elementScopedContacts) {
                            // Accepted Bridges can join previously separate elements. Use
                            // current connectivity, including all earlier synthetic faces.
                            var current = currentTopology;
                            var elements = ElementIds(current, token);
                            var closing = ClosingOwners(current, loops[loop], partner >= 0 ? loops[partner] : null, elements);
                            external = result.externalContacts.Fork(owners, elements, closing);
                        }
                        // Local/compound closure may append one patch before discovering a
                        // refusal on the next. Its geometry, patch IDs and contacts stay private.
                        var candidatePositions = pWeld; var candidateExact = exact;
                        var candidate = new List<int>(assembled); var stats = new Support { originalFaces = result.originalFaces };
                        RemeshBridge.SearchReport bridgeReport = null;
                        if (partner >= 0) {
                            bridgeAttempted = true;
                            bridgeReport = new RemeshBridge.SearchReport();
                            var patch = RemeshBridge.Generate(candidatePositions,candidate.ToArray(),loops[loop],loops[partner],token,ref contactTrials,out int tested, external,bridgeReport);
                            stats.contactTests += tested; candidate.AddRange(patch); stats.patchEnds.Add(candidate.Count / 3);
                        }
                        else if (mode == RemeshClosureMode.SurfaceCaps) {
                            var patch = RemeshSurfaceCap.Generate(candidatePositions, candidate.ToArray(), loops[loop], token,
                                ref contactTrials, out int tested, external);
                            stats.contactTests += tested; candidate.AddRange(patch); stats.patchEnds.Add(candidate.Count / 3);
                        }
                        else if (!result.bridgeCapFallbacks.ContainsKey(loop) && (localPlanes || mode == RemeshClosureMode.Automatic))
                            CloseLocal(ref candidatePositions, ref candidateExact, candidate, loops[loop], token, stats, ref contactTrials, planeTolerance, external);
                        else {
                            candidate.AddRange(Triangulate(candidatePositions, candidateExact, loops[loop], token, planeTolerance));
                            AuditContacts(candidatePositions, candidateExact, candidate.ToArray(), assembled.Count / 3, token, ref contactTrials, out int tested, external);
                            stats.contactTests += tested; stats.patchEnds.Add(candidate.Count / 3);
                        }
                        if (candidate.Count - iWeld.Length > MaxLoopEdges * 3 * 8) throw Refuse("total Cap face budget exceeded");
                        int removed = loops[loop].Count + (partner < 0 ? 0 : loops[partner].Count);
                        foreach (int accepted in closedLoops) removed += loops[accepted].Count;
                        var candidateTopology = RemeshTopology.Inspect(candidatePositions, candidate.ToArray(), token);
                        if (!candidateTopology.Valid || candidateTopology.boundary.Count != topology.boundary.Count - removed)
                            throw Refuse("closure candidate topology: " + candidateTopology.Description);
                        pWeld = candidatePositions; exact = candidateExact; assembled = candidate;
                        currentTopology = candidateTopology;
                        result.patchEnds.AddRange(stats.patchEnds); result.contactTests += stats.contactTests;
                        result.localPatches += stats.localPatches; result.planeRechecks += stats.planeRechecks;
                        result.externalContacts?.Merge(external);
                        if (partner >= 0) {
                            result.bridgePartners.Add(loop,partner); result.bridgePartners.Add(partner,loop);
                            result.bridgeSearch.Add(loop,bridgeReport.Description); result.bridgeSearch.Add(partner,bridgeReport.Description);
                        }
                        closedLoops.Add(loop); if (partner >= 0) closedLoops.Add(partner);
                        break;
                    }
                    catch (InvalidOperationException failure) when (continueOnRefusal || bridgeAttempted && bridgeCapFallback) {
                        if (bridgeAttempted && bridgeCapFallback && contactTrials <= MaxPairTrials) {
                            // A refused Bridge published no geometry. Retry each rim as
                            // its own planar disk, sharing the remaining contact budget.
                            // The second rim is processed normally by the outer loop.
                            result.bridgeCapFallbacks.Add(loop, failure.Message);
                            result.bridgeCapFallbacks.Add(partner, failure.Message);
                            partner = -1;
                            continue;
                        }
                        if (!continueOnRefusal) throw;
                        result.loopFailures.Add(loop, failure.Message);
                        if (partner >= 0) result.loopFailures.Add(partner, failure.Message);
                        break;
                    }
                }
                finished.Add(loop);
                if (partner >= 0) finished.Add(partner);
                loopCompleted?.Invoke(loop, finished.Count, chosen.Count);
            }
            var allIndices = assembled.ToArray();
            var after = RemeshTopology.Inspect(pWeld, allIndices, token);
            int removedEdges = 0; foreach (int loop in closedLoops) removedEdges += loops[loop].Count;
            if (!after.Valid || after.boundary.Count != topology.boundary.Count - removedEdges)
                throw Refuse("assembled Cap topology: " + after.Description);
            result.remainingBoundaryEdges = after.boundary.Count;
            if (elementScopedContacts) result.faceElements = ElementIds(after, token);
            result.positions = pWeld; result.indices = allIndices; result.addedFaces = (allIndices.Length - iWeld.Length) / 3;
            result.facePatches = new int[allIndices.Length / 3];
            int patchStart = result.originalFaces;
            for (int patch = 0; patch < result.patchEnds.Count; ++patch) {
                for (int f = patchStart; f < result.patchEnds[patch]; ++f) result.facePatches[f] = patch + 1;
                patchStart = result.patchEnds[patch];
            }
            return result;
        }

        static int[] ElementIds(RemeshTopology.Snapshot topology, CancellationToken token)
        {
            var elements = new int[topology.indices.Length / 3];
            for (int face = 0; face < elements.Length; ++face) {
                if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                elements[face] = topology.components.Find(face);
            }
            return elements;
        }

        static HashSet<int> ClosingOwners(RemeshTopology.Snapshot topology, List<int> first, List<int> second, int[] faceOwners)
        {
            if (faceOwners == null) return null;
            var owners = new HashSet<int>();
            foreach (var loop in new[] { first, second }) {
                if (loop == null) continue;
                for (int i = 0; i < loop.Count; ++i) {
                    int face = topology.edges[EdgeKey(topology.slots[loop[i]], topology.slots[loop[(i + 1) % loop.Count]])].firstFace;
                    if (faceOwners[face] < 0) return null;
                    owners.Add(faceOwners[face]);
                }
            }
            return owners;
        }

        static void CloseLocal(ref Vector3[] p, ref RemeshCapIntersection.Q[][] exact, List<int> assembled,
            List<int> loop, CancellationToken token, Support result, ref int contactTrials, double planeTolerance, ExternalContacts external)
        {
            var analysis = RemeshCapPlanes.Analyze(p, loop, token, minimumTolerance: planeTolerance); ++result.planeRechecks;
            if (analysis.kind == RemeshCapPlanes.Kind.Planar) {
                AppendPatch(p, exact, assembled, loop, true, token, result, ref contactTrials, planeTolerance, external);
                return;
            }
            if (analysis.kind != RemeshCapPlanes.Kind.TwoPlanes) {
                var compound = RemeshCompoundCap.Generate(p,assembled.ToArray(),loop,token,ref contactTrials,planeTolerance, external);
                p = compound.positions; exact = new RemeshCapIntersection.Q[p.Length][];
                for (int i=0;i<p.Length;++i) exact[i]=RemeshCapIntersection.Point(p[i]);
                foreach (var patch in compound.patches) { assembled.AddRange(patch); result.patchEnds.Add(assembled.Count/3); ++result.localPatches; ++result.planeRechecks; }
                result.contactTests += compound.contacts;
                return;
            }
            var arc = analysis.firstArc;
            AppendPatch(p, exact, assembled, arc, false, token, result, ref contactTrials, planeTolerance, external);
            // Accepting the first patch changes the actual halfedge contour. Use
            // that new topology, not the stale second arc or guessed loop number.
            var remaining = RemainingContour(p, assembled, arc, token);
            var next = RemeshCapPlanes.Analyze(p, remaining, token, minimumTolerance: planeTolerance); ++result.planeRechecks;
            if (next.kind != RemeshCapPlanes.Kind.Planar) throw Refuse("the remaining local contour is not wholly planar after the first patch");
            AppendPatch(p, exact, assembled, remaining, true, token, result, ref contactTrials, planeTolerance, external);
        }

        internal static List<int> RemainingContour(Vector3[] p, List<int> assembled, List<int> arc, CancellationToken token)
        {
            var fresh = Boundaries(RemeshTopology.Inspect(p, assembled.ToArray(), token), token, false);
            List<int> remaining = null; int a = arc[0], b = arc[arc.Count - 1];
            foreach (var candidate in fresh) for (int i = 0; i < candidate.Count; ++i) {
                int x = candidate[i], y = candidate[(i + 1) % candidate.Count];
                if (x != a || y != b) continue;
                if (remaining != null) throw Refuse("the new closure chord belongs to more than one boundary");
                remaining = candidate;
            }
            if (remaining == null) throw Refuse("the new closure chord is missing from the remaining contour");
            return remaining;
        }

        static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

        static HashSet<(int, int)> BoundaryEdges(RemeshTopology.Snapshot topology)
        {
            var result = new HashSet<(int, int)>();
            foreach (var pair in topology.edges) if (pair.Value.count == 1) result.Add(pair.Key);
            return result;
        }

        internal static void AppendPatch(Vector3[] p, RemeshCapIntersection.Q[][] exact, List<int> assembled,
            List<int> arc, bool closed, CancellationToken token, Support result, ref int trials, double planeTolerance, ExternalContacts external)
        {
            int oldFaces = assembled.Count / 3;
            var before = RemeshTopology.Inspect(p, assembled.ToArray(), token);
            var expected = BoundaryEdges(before);
            int edgeCount = closed ? arc.Count : arc.Count - 1;
            // The arc is an ordered portion of the freshly extracted boundary.
            // Its endpoints share one new chord; no other rim edge may change.
            for (int i = 0; i < edgeCount; ++i) {
                int a = arc[i], b = arc[(i + 1) % arc.Count];
                var edgeKey = EdgeKey(before.slots[a], before.slots[b]);
                var edge = before.edges[edgeKey];
                var ix = before.indices; int f = edge.firstFace;
                bool directed = false;
                for (int k = 0; k < 3; ++k) if (ix[f * 3 + k] == a && ix[f * 3 + (k + 1) % 3] == b) directed = true;
                if (edge.count != 1 || !directed || !expected.Remove(edgeKey))
                    throw Refuse("local patch support is not a continuous directed boundary arc");
            }
            if (!closed && !expected.Add(EdgeKey(before.slots[arc[0]], before.slots[arc[arc.Count - 1]])))
                throw Refuse("local closure chord is already a boundary edge");
            var added = Triangulate(p, exact, arc, token, planeTolerance);
            if (assembled.Count + added.Length - result.originalFaces * 3 > MaxLoopEdges * 3 * 8)
                throw Refuse("total Cap face budget exceeded");
            var candidate = new List<int>(assembled); candidate.AddRange(added);
            var all = candidate.ToArray(); var after = RemeshTopology.Inspect(p, all, token);
            if (!after.Valid || !BoundaryEdges(after).SetEquals(expected))
                throw Refuse("local patch changes an unselected boundary or has invalid topology: " + after.Description);
            AuditContacts(p, exact, all, oldFaces, token, ref trials, out int contacts, external);
            result.contactTests += contacts;
            assembled.AddRange(added); ++result.localPatches;
            result.patchEnds.Add(assembled.Count / 3);
        }

        // All vertices must have one fan (the preflight above). Each boundary
        // slot then has exactly one incoming/outgoing halfedge, no junction pairing.
        internal static List<List<int>> Boundaries(RemeshTopology.Snapshot topology, CancellationToken token, bool limitLoopEdges = true)
        {
            var next = new SortedDictionary<int, int>(); var incoming = new HashSet<int>();
            var ix = topology.indices;
            for (int f = 0; f < ix.Length / 3; ++f) {
                if ((f & 1023) == 0) token.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; ++k) {
                    int a = ix[f * 3 + k], b = ix[f * 3 + (k + 1) % 3];
                    var key = EdgeKey(topology.slots[a], topology.slots[b]);
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
                    if (limitLoopEdges && loop.Count > MaxLoopEdges) throw Refuse("a boundary loop exceeds 512 edges");
                } while (current != start);
                if (loop.Count < 3) throw Refuse("boundary cycle has fewer than three vertices");
                loops.Add(loop);
            }
            return loops;
        }

        static SortedSet<int> Selection(string text, int count, bool partial, out string warning)
        {
            var result = new SortedSet<int>();
            warning = null;
            if (string.Equals(text?.Trim(), "all", StringComparison.OrdinalIgnoreCase)) {
                for (int loop = 0; loop < count; ++loop) result.Add(loop);
                return result;
            }
            var invalid = new List<string>();
            foreach (string part in (text ?? "").Split(',')) {
                if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int loop) || loop < 0 || loop >= count || !result.Add(loop)) {
                    if (!partial) throw Refuse($"disk selection '{text}' must list unique loop numbers from 0 to {count - 1}");
                    invalid.Add("'" + part.Trim() + "'");
                }
            }
            if (invalid.Count > 0) warning = $"Invalid or repeated closure loop entries: {string.Join(", ", invalid)}. " +
                $"Available loop numbers: 0..{count - 1}. Invalid entries were skipped; no other contours were selected automatically.";
            return result;
        }

        internal static int[] Triangulate(Vector3[] p, RemeshCapIntersection.Q[][] exact, List<int> loop, CancellationToken token, double planeTolerance = 0)
        {
            if (!RemeshCapPlanes.TryPlane(p, loop, -1, true, out var plane, planeTolerance))
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

        internal static void AuditContacts(Vector3[] positions, RemeshCapIntersection.Q[][] exact, int[] ix, int originalFaces,
            CancellationToken token, ref int trials, out int tests, ExternalContacts external = null)
        {
            int faces = ix.Length / 3; tests = 0;
            var low = new Vector3[faces]; var high = new Vector3[faces];
            for (int f = 0; f < faces; ++f) {
                var a = positions[ix[f * 3]]; var b = positions[ix[f * 3 + 1]]; var c = positions[ix[f * 3 + 2]];
                low[f] = Vector3.Min(a, Vector3.Min(b, c)); high[f] = Vector3.Max(a, Vector3.Max(b, c));
            }
            List<int> protectedFaces = null;
            if (external?.faceElements != null) {
                protectedFaces = new List<int>(faces);
                for (int face = 0; face < faces; ++face) {
                    if ((face & 1023) == 0) token.ThrowIfCancellationRequested();
                    if (!external.IsExternal(face)) protectedFaces.Add(face);
                }
                external.excludedElementPairs += (long)(faces - protectedFaces.Count) * (faces - originalFaces);
            }
            for (int f = originalFaces; f < faces; ++f) for (int k = 0; k < (protectedFaces?.Count ?? f); ++k) {
                int g = protectedFaces == null ? k : protectedFaces[k];
                if (g >= f) break;
                if ((trials & 255) == 0) token.ThrowIfCancellationRequested();
                if (++trials > MaxPairTrials) throw Refuse("Cap contact audit exceeds the pair budget");
                if (low[f].x > high[g].x || low[g].x > high[f].x || low[f].y > high[g].y || low[g].y > high[f].y || low[f].z > high[g].z || low[g].z > high[f].z) continue;
                ++tests;
                var a = new[] { exact[ix[f * 3]], exact[ix[f * 3 + 1]], exact[ix[f * 3 + 2]] };
                var b = new[] { exact[ix[g * 3]], exact[ix[g * 3 + 1]], exact[ix[g * 3 + 2]] };
                if (!RemeshCapIntersection.Improper(a, b)) continue;
                if (external != null && external.IsExternal(g)) { external.Add(f, g); continue; }
                throw Refuse($"new face {f} contacts face {g} beyond their shared vertex/edge");
            }
        }

        static InvalidOperationException Refuse(string reason) => new InvalidOperationException("Planar Cap refused: " + reason + ". Source donors were preserved.");
    }
}
