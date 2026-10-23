// FbxCornerMatch.cs — maps the polygon corners of an FBX mesh to the vertices of a
// Unity mesh built from it, so a per-vertex channel the tool changed can be written
// back into the FBX corner by corner without touching its polygons.
//
// Plain arrays in, plain arrays out: no UnityEngine and no FBX SDK types, so the
// matching is testable on its own. Positions of the corners come from a tagged import
// of the same file (FbxExport.WriteChannels), so they are in the result mesh's space
// and bit-identical to its vertices whatever axis, unit or pivot conversion Unity did.

using System;
using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    internal static class FbxCornerMatch
    {
        internal sealed class Result
        {
            /// <summary>Result-mesh vertex per FBX corner; -1 when unresolved or conflicting.</summary>
            public int[] cornerToVertex;
            /// <summary>Corners with a position but no result face of their polygon.</summary>
            public int unresolved;
            /// <summary>Corners whose candidate vertices disagree on a channel being written.</summary>
            public int conflicts;
            public bool[] conflictingCorners;
            /// <summary>Corners the tagged import has no vertex for (degenerate polygons).</summary>
            public int missing;
            /// <summary>
            /// Corners of polygons with fewer than three distinct positions whose candidates
            /// disagree; such a polygon covers no area, so they take the first candidate.
            /// </summary>
            public int zeroAreaChoices;
        }

        internal readonly struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
        {
            readonly int x, y, z;
            public PositionKey(float px, float py, float pz) { x = Bits(px); y = Bits(py); z = Bits(pz); }
            // -0 and +0 are the same position.
            static int Bits(float v) => v == 0f ? 0 : BitConverter.SingleToInt32Bits(v);
            public bool Equals(PositionKey o) => x == o.x && y == o.y && z == o.z;
            public override bool Equals(object obj) => obj is PositionKey k && Equals(k);
            public int CompareTo(PositionKey o) => x != o.x ? x.CompareTo(o.x) : y != o.y ? y.CompareTo(o.y) : z.CompareTo(o.z);
            public override int GetHashCode() => unchecked((x * 73856093) ^ (y * 19349663) ^ (z * 83492791));
            public static bool operator ==(PositionKey a, PositionKey b) => a.Equals(b);
            public static bool operator !=(PositionKey a, PositionKey b) => !a.Equals(b);
            public static bool operator <(PositionKey a, PositionKey b) => a.CompareTo(b) < 0;
            public static bool operator <=(PositionKey a, PositionKey b) => a.CompareTo(b) <= 0;
            public static bool operator >(PositionKey a, PositionKey b) => a.CompareTo(b) > 0;
            public static bool operator >=(PositionKey a, PositionKey b) => a.CompareTo(b) >= 0;
        }

        // The distinct corner positions of a face, sorted: a face lies on a polygon exactly when
        // this set is a subset of the polygon's positions.
        readonly struct FaceKey : IEquatable<FaceKey>
        {
            readonly PositionKey a, b, c, d;
            readonly int count;

            public FaceKey(List<PositionKey> sorted)
            {
                count = sorted.Count;
                a = sorted[0];
                b = count > 1 ? sorted[1] : default;
                c = count > 2 ? sorted[2] : default;
                d = count > 3 ? sorted[3] : default;
            }

            public bool Equals(FaceKey o) => count == o.count && a.Equals(o.a) && b.Equals(o.b) && c.Equals(o.c) && d.Equals(o.d);
            public override bool Equals(object obj) => obj is FaceKey k && Equals(k);
            public override int GetHashCode() => unchecked(((a.GetHashCode() * 31 + b.GetHashCode()) * 31 + c.GetHashCode()) * 31 + d.GetHashCode() + count);
        }

        readonly struct Candidate
        {
            public readonly int vertex, orientation;
            /// <summary>The face has fewer than three distinct positions.</summary>
            public readonly bool degenerate;
            /// <summary>Records a candidate result vertex and its polygon-normal or imported-triangle orientation.</summary>
            public Candidate(int vertex, int orientation, bool degenerate)
            {
                this.vertex = vertex; this.orientation = orientation; this.degenerate = degenerate;
            }
        }

        /// <summary>Maps polygon corners to compatible result vertices, reporting missing, unresolved and conflicting values separately.</summary>
        /// <param name="polygonSizes">Corner count of each FBX polygon, in polygon order.</param>
        /// <param name="cornerPositions">xyz per corner in result-mesh space; NaN x for a corner the tagged import has no vertex for.</param>
        /// <param name="positions">xyz per result-mesh vertex.</param>
        /// <param name="faceIndices">Result-mesh faces, flattened; <paramref name="faceSizes"/> gives each face's corner count.</param>
        /// <param name="faceSizes">Corner count of each result-mesh face.</param>
        /// <param name="signatureMatches">(corner, vertex) → whether attributes the write leaves alone agree; null skips the check.</param>
        /// <param name="sameWrittenValues">(vertex, vertex) → whether two vertices carry the same values in every channel being written.</param>
        /// <param name="importedTriangles">Optional tagged-import triangle indices in FBX corner space, with Unity's imported winding.</param>
        internal static Result Match(int[] polygonSizes, float[] cornerPositions, float[] positions,
            int[] faceIndices, int[] faceSizes, Func<int, int, bool> signatureMatches, Func<int, int, bool> sameWrittenValues,
            int[] importedTriangles = null)
        {
            if (polygonSizes == null) throw new ArgumentNullException(nameof(polygonSizes));
            if (cornerPositions == null) throw new ArgumentNullException(nameof(cornerPositions));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (faceIndices == null || faceSizes == null) throw new ArgumentNullException(nameof(faceIndices));
            if (sameWrittenValues == null) throw new ArgumentNullException(nameof(sameWrittenValues));
            return new Matcher(cornerPositions, positions, faceIndices, faceSizes, importedTriangles).Run(polygonSizes, signatureMatches, sameWrittenValues);
        }

        sealed class Matcher
        {
            // +/-1 compares with the polygon normal; +/-2 compares directly with
            // the imported triangle, which already includes Unity's axis conversion.
            const int ImportedSameOrientation = 2;
            readonly float[] cornerPositions, positions;
            readonly int[] faceIndices, faceSizes, faceStarts;
            readonly Dictionary<PositionKey, List<(int face, int slot)>> facesAt = new Dictionary<PositionKey, List<(int face, int slot)>>();
            readonly Dictionary<FaceKey, List<int>> facesOn = new Dictionary<FaceKey, List<int>>();
            readonly List<PositionKey> subset = new List<PositionKey>(4);
            readonly List<int> polygonFaces = new List<int>();
            readonly List<Candidate>[] candidates;
            readonly bool[] degenerateFaces, zeroAreaCorners;
            readonly List<PositionKey> polygonKeys = new List<PositionKey>(8);
            readonly List<int> distinct = new List<int>(4);
            readonly Dictionary<int, List<int>> importedFaces = new Dictionary<int, List<int>>();
            readonly int[] importedTriangles;

            /// <summary>Indexes result faces and optional imported triangles without altering either input mesh.</summary>
            public Matcher(float[] cornerPositions, float[] positions, int[] faceIndices, int[] faceSizes, int[] importedTriangles)
            {
                this.cornerPositions = cornerPositions;
                this.positions = positions;
                this.faceIndices = faceIndices;
                this.faceSizes = faceSizes;
                this.importedTriangles = importedTriangles;
                if (importedTriangles != null)
                    for (int t = 0; t + 2 < importedTriangles.Length; t += 3)
                        for (int k = 0; k < 3; ++k)
                        {
                            int corner = importedTriangles[t + k];
                            if (!importedFaces.TryGetValue(corner, out var faces)) importedFaces[corner] = faces = new List<int>();
                            faces.Add(t);
                        }
                faceStarts = new int[faceSizes.Length];
                for (int f = 0, s = 0; f < faceSizes.Length; s += faceSizes[f], f++) faceStarts[f] = s;
                candidates = new List<Candidate>[cornerPositions.Length / 3];
                zeroAreaCorners = new bool[candidates.Length];
                degenerateFaces = new bool[faceSizes.Length];
                IndexFaces();
            }

            // Result faces by the position of each of their corners, and by their position set.
            void IndexFaces()
            {
                for (int f = 0; f < faceSizes.Length; f++)
                {
                    subset.Clear();
                    for (int j = 0; j < faceSizes[f]; j++)
                    {
                        var key = VertexKey(positions, faceIndices[faceStarts[f] + j]);
                        if (!facesAt.TryGetValue(key, out var list)) facesAt[key] = list = new List<(int, int)>(6);
                        list.Add((f, j));
                        if (!subset.Contains(key)) subset.Add(key);
                    }
                    degenerateFaces[f] = subset.Count < 3;
                    if (subset.Count > 4) continue; // only triangles and quads are indexed by set
                    subset.Sort();
                    var faceKey = new FaceKey(subset);
                    if (!facesOn.TryGetValue(faceKey, out var faces)) facesOn[faceKey] = faces = new List<int>(1);
                    faces.Add(f);
                }
            }

            /// <summary>Learns the import's winding relation, then resolves corner candidates using preserved attributes and written values.</summary>
            public Result Run(int[] polygonSizes, Func<int, int, bool> signatureMatches, Func<int, int, bool> sameWrittenValues)
            {
                // Pass 1: every face of the result mesh lying on the polygon's own corners is a
                // candidate for each corner it touches. Orientation is recorded, not yet used:
                // whether Unity's import kept or reversed the winding depends on its axis
                // conversion, so the relation is learned from the polygons whose faces all agree
                // (a double-sided duplicate has faces both ways and does not vote).
                int orientationVotes = 0;
                for (int p = 0, start = 0; p < polygonSizes.Length; start += polygonSizes[p], p++)
                    orientationVotes += CollectPolygon(start, polygonSizes[p]);
                // No polygon decides it: Unity mirrors X on import and reverses the winding to
                // keep faces facing out, so a face runs against its polygon's corner order.
                int relation = orientationVotes != 0 ? Math.Sign(orientationVotes) : -1;

                // Pass 2: drop back-facing duplicates, then settle each corner.
                var result = new Result { cornerToVertex = new int[candidates.Length], conflictingCorners = new bool[candidates.Length] };
                for (int c = 0; c < candidates.Length; c++)
                    result.cornerToVertex[c] = Settle(c, relation, signatureMatches, sameWrittenValues, result);
                return result;
            }

            /// <summary>Records candidates for one polygon and returns its aggregate-normal winding vote.</summary>
            int CollectPolygon(int start, int size)
            {
                polygonKeys.Clear();
                bool complete = true;
                for (int k = 0; k < size; k++)
                    if (HasPosition(cornerPositions, start + k))
                    {
                        var key = CornerKey(cornerPositions, start + k);
                        if (!polygonKeys.Contains(key)) polygonKeys.Add(key);
                    }
                    else complete = false;
                for (int k = 0; k < size; k++) zeroAreaCorners[start + k] = complete && polygonKeys.Count < 3;
                Normal(cornerPositions, start, size, out double nx, out double ny, out double nz);

                int forward = 0, backward = 0;
                foreach (int face in FacesOnPolygon())
                {
                    int orientation = Orientation(positions, faceIndices, faceStarts[face], faceSizes[face], nx, ny, nz);
                    int imported = ImportedOrientation(face, start, size);
                    for (int j = 0; j < faceSizes[face]; j++)
                    {
                        int vertex = faceIndices[faceStarts[face] + j];
                        var key = VertexKey(positions, vertex);
                        for (int c = start; c < start + size; c++)
                            if (HasPosition(cornerPositions, c) && CornerKey(cornerPositions, c).Equals(key))
                                (candidates[c] ??= new List<Candidate>(2)).Add(new Candidate(vertex,
                                    imported != 0 ? imported * ImportedSameOrientation : orientation, degenerateFaces[face]));
                    }
                    if (orientation > 0) forward++;
                    else if (orientation < 0) backward++;
                }
                if (forward == 0 && backward > 0) return -1;
                return backward == 0 && forward > 0 ? 1 : 0;
            }

            /// <summary>
            /// Compares an exact imported triangle's cyclic order, including folded
            /// polygons whose triangle disagrees with the Newell normal. Returns zero
            /// when the imported correspondence is absent or ambiguous.
            /// </summary>
            int ImportedOrientation(int face, int start, int size)
            {
                if (importedTriangles == null || faceSizes[face] != 3) return 0;
                int result = 0;
                var a = VertexKey(positions, faceIndices[faceStarts[face]]);
                var b = VertexKey(positions, faceIndices[faceStarts[face] + 1]);
                var c = VertexKey(positions, faceIndices[faceStarts[face] + 2]);
                if (a == b || b == c || c == a) return 0;
                for (int corner = start; corner < start + size; ++corner)
                {
                    if (!HasPosition(cornerPositions, corner) || CornerKey(cornerPositions, corner) != a
                        || !importedFaces.TryGetValue(corner, out var faces)) continue;
                    foreach (int t in faces)
                    {
                        int slot = -1;
                        bool inside = true;
                        for (int k = 0; k < 3; ++k)
                        {
                            int index = importedTriangles[t + k];
                            inside &= index >= start && index < start + size;
                            if (index == corner) slot = k;
                        }
                        if (!inside || slot < 0) continue;
                        var next = CornerKey(cornerPositions, importedTriangles[t + (slot + 1) % 3]);
                        var last = CornerKey(cornerPositions, importedTriangles[t + (slot + 2) % 3]);
                        int sign = next == b && last == c ? 1 : next == c && last == b ? -1 : 0;
                        if (sign == 0) continue;
                        if (result != 0 && result != sign) return 0;
                        result = sign;
                    }
                }
                return result;
            }

            // The result faces made only of the polygon's positions. Up to 8 distinct positions
            // the faces are looked up by every position subset a triangle or quad can have, so a
            // pole shared by many polygons costs nothing extra; larger polygons scan the faces
            // around their corners.
            List<int> FacesOnPolygon()
            {
                polygonFaces.Clear();
                int n = polygonKeys.Count;
                if (n <= 8)
                {
                    for (int mask = 1; mask < 1 << n; mask++)
                    {
                        if (BitCount(mask) > 4) continue;
                        subset.Clear();
                        for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) subset.Add(polygonKeys[i]);
                        subset.Sort();
                        if (facesOn.TryGetValue(new FaceKey(subset), out var faces)) polygonFaces.AddRange(faces);
                    }
                    return polygonFaces;
                }
                foreach (var key in polygonKeys)
                {
                    if (!facesAt.TryGetValue(key, out var touching)) continue;
                    foreach (var (face, _) in touching)
                        if (!polygonFaces.Contains(face) && FaceOnPolygon(positions, faceIndices, faceStarts[face], faceSizes[face], polygonKeys))
                            polygonFaces.Add(face);
                }
                return polygonFaces;
            }

            /// <summary>Counts selected polygon positions in a subset mask so only triangle and quad face keys are queried.</summary>
            static int BitCount(int v)
            {
                int count = 0;
                for (; v != 0; v &= v - 1) count++;
                return count;
            }

            // The vertex for corner c, or -1 with the reason counted in result.
            /// <summary>Filters winding and preserved attributes, then refuses incompatible written values at one corner.</summary>
            int Settle(int c, int relation, Func<int, int, bool> signatureMatches, Func<int, int, bool> sameWrittenValues, Result result)
            {
                if (!HasPosition(cornerPositions, c)) { result.missing++; return -1; }
                if (candidates[c] == null) { result.unresolved++; return -1; }

                // A face with fewer than three positions lies on every polygon sharing its
                // edge, so it stands in only where no face with area reaches the corner.
                bool solid = false;
                foreach (var cand in candidates[c]) solid |= Facing(cand, relation) && !cand.degenerate;
                distinct.Clear();
                foreach (var cand in candidates[c])
                    if (Facing(cand, relation) && (!solid || !cand.degenerate) && !distinct.Contains(cand.vertex))
                        distinct.Add(cand.vertex);
                if (distinct.Count > 1 && signatureMatches != null)
                {
                    var agreeing = distinct.FindAll(v => signatureMatches(c, v));
                    if (agreeing.Count > 0) { distinct.Clear(); distinct.AddRange(agreeing); }
                }
                if (distinct.Count == 0) { result.unresolved++; return -1; }

                int chosen = distinct[0];
                for (int i = 1; i < distinct.Count; i++)
                {
                    if (sameWrittenValues(chosen, distinct[i])) continue;
                    // A polygon of fewer than three positions shows none of its values.
                    if (zeroAreaCorners[c]) { result.zeroAreaChoices++; return chosen; }
                    result.conflicts++; result.conflictingCorners[c] = true; return -1;
                }
                return chosen;
            }

            static bool Facing(Candidate cand, int relation)
                => cand.orientation == ImportedSameOrientation || cand.orientation == 0 || cand.orientation == relation;

            static bool HasPosition(float[] p, int i) => !float.IsNaN(p[i * 3]);
            static PositionKey CornerKey(float[] p, int i) => new PositionKey(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            static PositionKey VertexKey(float[] p, int v) => new PositionKey(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);

            static bool FaceOnPolygon(float[] positions, int[] faceIndices, int start, int size, List<PositionKey> polygonKeys)
            {
                for (int j = 0; j < size; j++)
                    if (!polygonKeys.Contains(VertexKey(positions, faceIndices[start + j]))) return false;
                return true;
            }

            // Newell normal of the polygon's corners; zero when they are degenerate or missing.
            static void Normal(float[] p, int start, int size, out double nx, out double ny, out double nz)
            {
                nx = ny = nz = 0;
                for (int k = 0; k < size; k++)
                {
                    int a = start + k, b = start + (k + 1) % size;
                    if (!HasPosition(p, a) || !HasPosition(p, b)) { nx = ny = nz = 0; return; }
                    double ax = p[a * 3], ay = p[a * 3 + 1], az = p[a * 3 + 2];
                    double bx = p[b * 3], by = p[b * 3 + 1], bz = p[b * 3 + 2];
                    nx += (ay - by) * (az + bz);
                    ny += (az - bz) * (ax + bx);
                    nz += (ax - bx) * (ay + by);
                }
            }

            // +1 / -1 when the face's winding agrees / disagrees with the polygon normal, 0 when either is degenerate.
            static int Orientation(float[] positions, int[] faceIndices, int start, int size, double nx, double ny, double nz)
            {
                double fx = 0, fy = 0, fz = 0;
                for (int j = 0; j < size; j++)
                {
                    int a = faceIndices[start + j] * 3, b = faceIndices[start + (j + 1) % size] * 3;
                    double ax = positions[a], ay = positions[a + 1], az = positions[a + 2];
                    double bx = positions[b], by = positions[b + 1], bz = positions[b + 2];
                    fx += (ay - by) * (az + bz);
                    fy += (az - bz) * (ax + bx);
                    fz += (ax - bx) * (ay + by);
                }
                double dot = fx * nx + fy * ny + fz * nz;
                double scale = Math.Sqrt((fx * fx + fy * fy + fz * fz) * (nx * nx + ny * ny + nz * nz));
                if (scale <= 0 || Math.Abs(dot) <= 1e-9 * scale) return 0;
                return dot > 0 ? 1 : -1;
            }
        }
    }
}
