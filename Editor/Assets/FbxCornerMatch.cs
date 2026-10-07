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
            /// <summary>Corners the tagged import has no vertex for (degenerate polygons).</summary>
            public int missing;
        }

        readonly struct PositionKey : IEquatable<PositionKey>
        {
            readonly int x, y, z;
            public PositionKey(float px, float py, float pz) { x = Bits(px); y = Bits(py); z = Bits(pz); }
            // -0 and +0 are the same position.
            static int Bits(float v) => v == 0f ? 0 : BitConverter.SingleToInt32Bits(v);
            public bool Equals(PositionKey o) => x == o.x && y == o.y && z == o.z;
            public override bool Equals(object obj) => obj is PositionKey k && Equals(k);
            public override int GetHashCode() => unchecked((x * 73856093) ^ (y * 19349663) ^ (z * 83492791));
        }

        readonly struct Candidate
        {
            public readonly int vertex, orientation;
            public Candidate(int vertex, int orientation) { this.vertex = vertex; this.orientation = orientation; }
        }

        /// <param name="polygonSizes">Corner count of each FBX polygon, in polygon order.</param>
        /// <param name="cornerPositions">xyz per corner in result-mesh space; NaN x for a corner the tagged import has no vertex for.</param>
        /// <param name="positions">xyz per result-mesh vertex.</param>
        /// <param name="faceIndices">Result-mesh faces, flattened; <paramref name="faceSizes"/> gives each face's corner count.</param>
        /// <param name="signatureMatches">(corner, vertex) → whether attributes the write leaves alone agree; null skips the check.</param>
        /// <param name="sameWrittenValues">(vertex, vertex) → whether two vertices carry the same values in every channel being written.</param>
        internal static Result Match(int[] polygonSizes, float[] cornerPositions, float[] positions,
            int[] faceIndices, int[] faceSizes, Func<int, int, bool> signatureMatches, Func<int, int, bool> sameWrittenValues)
        {
            if (polygonSizes == null) throw new ArgumentNullException(nameof(polygonSizes));
            if (cornerPositions == null) throw new ArgumentNullException(nameof(cornerPositions));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            if (faceIndices == null || faceSizes == null) throw new ArgumentNullException(nameof(faceIndices));
            if (sameWrittenValues == null) throw new ArgumentNullException(nameof(sameWrittenValues));

            int cornerCount = cornerPositions.Length / 3;
            var faceStarts = new int[faceSizes.Length];
            for (int f = 0, s = 0; f < faceSizes.Length; s += faceSizes[f], f++) faceStarts[f] = s;

            // Result faces by the position of each of their corners.
            var facesAt = new Dictionary<PositionKey, List<(int face, int slot)>>();
            for (int f = 0; f < faceSizes.Length; f++)
                for (int j = 0; j < faceSizes[f]; j++)
                {
                    var key = VertexKey(positions, faceIndices[faceStarts[f] + j]);
                    if (!facesAt.TryGetValue(key, out var list)) facesAt[key] = list = new List<(int, int)>(6);
                    list.Add((f, j));
                }

            // Pass 1: every face of the result mesh lying on the polygon's own corners is a
            // candidate for each corner it touches. Orientation is recorded, not yet used:
            // whether Unity's import kept or reversed the winding depends on its axis
            // conversion, so the relation is learned from the polygons whose faces all agree
            // (a double-sided duplicate has faces both ways and does not vote).
            var candidates = new List<Candidate>[cornerCount];
            var result = new Result { cornerToVertex = new int[cornerCount] };
            int orientationVotes = 0;
            var polygonKeys = new List<PositionKey>(8);
            for (int p = 0, start = 0; p < polygonSizes.Length; start += polygonSizes[p], p++)
            {
                int size = polygonSizes[p];
                polygonKeys.Clear();
                for (int k = 0; k < size; k++)
                    if (HasPosition(cornerPositions, start + k)) polygonKeys.Add(CornerKey(cornerPositions, start + k));
                Normal(cornerPositions, start, size, out double nx, out double ny, out double nz);

                int forward = 0, backward = 0;
                for (int k = 0; k < size; k++)
                {
                    int c = start + k;
                    if (!HasPosition(cornerPositions, c)) continue;
                    if (!facesAt.TryGetValue(CornerKey(cornerPositions, c), out var touching)) continue;
                    foreach (var (face, slot) in touching)
                    {
                        if (!FaceOnPolygon(positions, faceIndices, faceStarts[face], faceSizes[face], polygonKeys)) continue;
                        int orientation = Orientation(positions, faceIndices, faceStarts[face], faceSizes[face], nx, ny, nz);
                        (candidates[c] ??= new List<Candidate>(2)).Add(new Candidate(faceIndices[faceStarts[face] + slot], orientation));
                        if (orientation > 0) forward++; else if (orientation < 0) backward++;
                    }
                }
                if (forward == 0 && backward > 0) orientationVotes--;
                else if (backward == 0 && forward > 0) orientationVotes++;
            }
            // No polygon decides it: Unity mirrors X on import and reverses the winding to
            // keep faces facing out, so a face runs against its polygon's corner order.
            int relation = orientationVotes != 0 ? Math.Sign(orientationVotes) : -1;

            // Pass 2: drop back-facing duplicates, then settle each corner.
            var distinct = new List<int>(4);
            for (int c = 0; c < cornerCount; c++)
            {
                result.cornerToVertex[c] = -1;
                if (!HasPosition(cornerPositions, c)) { result.missing++; continue; }
                var list = candidates[c];
                if (list == null) { result.unresolved++; continue; }

                distinct.Clear();
                foreach (var cand in list)
                    if ((cand.orientation == 0 || cand.orientation == relation) && !distinct.Contains(cand.vertex))
                        distinct.Add(cand.vertex);
                if (distinct.Count > 1 && signatureMatches != null)
                {
                    int corner = c;
                    var agreeing = distinct.FindAll(v => signatureMatches(corner, v));
                    if (agreeing.Count > 0) { distinct.Clear(); distinct.AddRange(agreeing); }
                }
                if (distinct.Count == 0) { result.unresolved++; continue; }

                int chosen = distinct[0];
                bool agree = true;
                for (int i = 1; i < distinct.Count && agree; i++) agree = sameWrittenValues(chosen, distinct[i]);
                if (!agree) { result.conflicts++; continue; }
                result.cornerToVertex[c] = chosen;
            }
            return result;
        }

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
